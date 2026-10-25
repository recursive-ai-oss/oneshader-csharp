// Runs one parity job (compute dispatches or one offscreen draw) on WebGPU and returns the outputs.
// Test tooling only: the job format is written by tests/OneShader.ParityTests/WebGpuParityTests.cs.
//
// Orientation: WebGPU's clip-space y points up, like the Vulkan reference run, which the test draws with a
// negative-height viewport. Both therefore see the same clip coordinates, framebuffer rows and front faces.

const BYTES_PER_TEXEL = { r8unorm: 1, rgba8unorm: 4, r32float: 4, rgba32float: 16, r32uint: 4, rgba16float: 8, depth32float: 4 };
const VERTEX_FORMATS = {
  float1: 'float32', float2: 'float32x2', float3: 'float32x3', float4: 'float32x4',
  unorm8x4: 'unorm8x4', word: 'uint32', uint4: 'uint32x4',
};
const ALL_STAGES = GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT | GPUShaderStage.COMPUTE;

let adapter = null;
let device = null;
let sink = null;

function fromBase64(text) {
  const bin = atob(text);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
}

function toBase64(bytes) {
  let out = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    out += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  }
  return btoa(out);
}

const align = (n, a) => Math.ceil(n / a) * a;

async function init() {
  if (device) return;
  if (!navigator.gpu) throw new Error('navigator.gpu is missing');
  adapter = await navigator.gpu.requestAdapter({ powerPreference: 'high-performance' });
  if (!adapter) throw new Error('no WebGPU adapter');
  // Default limits and no optional features: what a portable web build gets.
  device = await adapter.requestDevice();
  device.addEventListener('uncapturederror', (e) => sink?.push('uncaptured: ' + e.error.message));
  device.lost.then((info) => sink?.push('device lost: ' + info.message));
}

window.adapterInfo = async () => {
  await init();
  const i = adapter.info;
  return {
    vendor: i.vendor, architecture: i.architecture, device: i.device, description: i.description,
    isFallbackAdapter: adapter.isFallbackAdapter ?? i.isFallbackAdapter,
    features: [...adapter.features].sort(),
    userAgent: navigator.userAgent,
  };
};

// Global resource declarations of a WGSL module: @group(g) @binding(b) var<space, access> name : type;
function parseGlobals(wgsl) {
  const re = /@group\((\d+)\)\s*@binding\((\d+)\)\s*var(?:<([^>]*)>)?\s+(\w+)\s*:\s*([^;]+);/g;
  const found = [];
  for (const m of wgsl.matchAll(re)) {
    found.push({ group: +m[1], binding: +m[2], space: (m[3] ?? '').split(',').map((s) => s.trim()), name: m[4], type: m[5].trim() });
  }
  return found;
}

function layoutEntryFor(decl, res, stageBits) {
  const e = { binding: decl.binding, visibility: stageBits };
  const t = decl.type;
  if (decl.space[0] === 'uniform') {
    e.buffer = { type: 'uniform' };
  } else if (decl.space[0] === 'storage') {
    e.buffer = { type: decl.space[1] === 'read_write' ? 'storage' : 'read-only-storage' };
  } else if (t.startsWith('texture_storage')) {
    const m = /texture_storage_(2d|2d_array)<(\w+),\s*(\w+)>/.exec(t);
    e.storageTexture = {
      access: { write: 'write-only', read: 'read-only', read_write: 'read-write' }[m[3]],
      format: m[2],
      viewDimension: m[1] === '2d' ? '2d' : '2d-array',
    };
  } else if (t.startsWith('texture_depth')) {
    e.texture = { sampleType: 'depth', viewDimension: viewDimensionOf(t) };
  } else if (t.startsWith('texture_')) {
    let sampleType = 'float';
    if (/<u32>/.test(t)) sampleType = 'uint';
    else if (/<i32>/.test(t)) sampleType = 'sint';
    else if (res && (res.texture.format === 'r32float' || res.texture.format === 'rgba32float' || res.texture.format === 'depth32float')) {
      sampleType = 'unfilterable-float';
    }
    e.texture = { sampleType, viewDimension: viewDimensionOf(t) };
  } else if (t === 'sampler_comparison') {
    e.sampler = { type: 'comparison' };
  } else if (t === 'sampler') {
    e.sampler = { type: res && res.sampler.linear ? 'filtering' : 'non-filtering' };
  } else {
    throw new Error(`unsupported resource declaration '${decl.name}: ${t}'`);
  }
  return e;
}

function viewDimensionOf(t) {
  if (t.includes('cube_array')) return 'cube-array';
  if (t.includes('cube')) return 'cube';
  if (t.includes('2d_array')) return '2d-array';
  return '2d';
}

function minBindingSize(decl) {
  // Uniform buffers are arrays of vec4<u32> rows; the binding must cover all of them.
  const m = /array<vec4<u32>,\s*(\d+)\s*>/.exec(decl.type);
  return m ? (+m[1]) * 16 : 16;
}

function makeBuffer(usage, bytes, minSize = 0) {
  const size = align(Math.max(bytes.length, minSize, 4), 16);
  const buffer = device.createBuffer({ size, usage: usage | GPUBufferUsage.COPY_DST });
  device.queue.writeBuffer(buffer, 0, bytes);
  return buffer;
}

function makeTexture(res, usage) {
  const t = res.texture;
  return device.createTexture({
    size: [t.width, t.height, Math.max(t.layers, 1)],
    format: t.format,
    mipLevelCount: t.mips,
    usage,
  });
}

function uploadTexture(res, texture, bytes) {
  const t = res.texture;
  const bpt = BYTES_PER_TEXEL[t.format];
  const layers = Math.max(t.layers, 1);
  let offset = 0;
  for (let level = 0; level < t.mips; level++) {
    const w = Math.max(t.width >> level, 1);
    const h = Math.max(t.height >> level, 1);
    const n = w * h * layers * bpt;
    device.queue.writeTexture({ texture, mipLevel: level }, bytes.subarray(offset, offset + n),
      { bytesPerRow: w * bpt, rowsPerImage: h }, [w, h, layers]);
    offset += n;
  }
}

// Depth textures cannot be filled by a copy, so the values go in through an r32float texture and a full-screen pass.
function uploadDepthTexture(res, texture, bytes, encoder) {
  const t = res.texture;
  const layers = Math.max(t.layers, 1);
  const module = device.createShaderModule({ code: `
    @group(0) @binding(0) var src: texture_2d<f32>;
    @vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
      let p = vec2<f32>(f32((i << 1u) & 2u), f32(i & 2u));
      return vec4<f32>(p * 2.0 - 1.0, 0.0, 1.0);
    }
    struct Out { @builtin(frag_depth) d: f32 }
    @fragment fn fs(@builtin(position) pos: vec4<f32>) -> Out {
      return Out(textureLoad(src, vec2<i32>(pos.xy), 0).x);
    }` });
  const pipeline = device.createRenderPipeline({
    layout: 'auto',
    vertex: { module, entryPoint: 'vs' },
    fragment: { module, entryPoint: 'fs', targets: [] },
    depthStencil: { format: 'depth32float', depthWriteEnabled: true, depthCompare: 'always' },
  });
  let offset = 0;
  for (let level = 0; level < t.mips; level++) {
    const w = Math.max(t.width >> level, 1);
    const h = Math.max(t.height >> level, 1);
    for (let layer = 0; layer < layers; layer++) {
      const n = w * h * 4;
      const src = device.createTexture({ size: [w, h], format: 'r32float', usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
      device.queue.writeTexture({ texture: src }, bytes.subarray(offset, offset + n), { bytesPerRow: w * 4 }, [w, h]);
      offset += n;
      const pass = encoder.beginRenderPass({
        colorAttachments: [],
        depthStencilAttachment: {
          view: texture.createView({ baseMipLevel: level, mipLevelCount: 1, baseArrayLayer: layer, arrayLayerCount: 1 }),
          depthClearValue: 1, depthLoadOp: 'clear', depthStoreOp: 'store',
        },
      });
      pass.setPipeline(pipeline);
      pass.setBindGroup(0, device.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries: [{ binding: 0, resource: src.createView() }] }));
      pass.draw(3);
      pass.end();
    }
  }
}

// Reads a texture back into tightly packed rows.
function copyTextureOut(encoder, texture, w, h, layers, bpt, aspect = 'all') {
  const rowBytes = align(w * bpt, 256);
  const buffer = device.createBuffer({ size: rowBytes * h * layers, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
  encoder.copyTextureToBuffer({ texture, aspect }, { buffer, bytesPerRow: rowBytes, rowsPerImage: h }, [w, h, layers]);
  return { buffer, rowBytes, w, h, layers, bpt };
}

async function readTexture(rb) {
  await rb.buffer.mapAsync(GPUMapMode.READ);
  const padded = new Uint8Array(rb.buffer.getMappedRange());
  const tight = rb.w * rb.bpt;
  const out = new Uint8Array(tight * rb.h * rb.layers);
  for (let layer = 0; layer < rb.layers; layer++) {
    for (let row = 0; row < rb.h; row++) {
      const src = (layer * rb.h + row) * rb.rowBytes;
      out.set(padded.subarray(src, src + tight), (layer * rb.h + row) * tight);
    }
  }
  rb.buffer.unmap();
  return out;
}

function blendState(mode) {
  if (mode === 'none') return undefined;
  const color = { operation: 'add', srcFactor: mode === 'alpha' ? 'src-alpha' : 'one', dstFactor: 'one-minus-src-alpha' };
  const alpha = { operation: 'add', srcFactor: 'one', dstFactor: 'one-minus-src-alpha' };
  return { color, alpha };
}

async function execute(job, errors) {
  const stageBits = job.kind === 'compute' ? GPUShaderStage.COMPUTE : ALL_STAGES;

  // Shader modules and their resource declarations.
  const modules = {};
  const decls = new Map();
  for (const m of job.modules) {
    const module = device.createShaderModule({ code: m.wgsl });
    const info = await module.getCompilationInfo();
    for (const msg of info.messages) {
      if (msg.type === 'error') errors.push(`${m.entry}: WGSL ${msg.lineNum}:${msg.linePos}: ${msg.message}`);
    }
    modules[m.entry] = module;
    const bit = m.stage === 'vertex' ? GPUShaderStage.VERTEX : m.stage === 'pixel' ? GPUShaderStage.FRAGMENT : GPUShaderStage.COMPUTE;
    for (const d of parseGlobals(m.wgsl)) {
      const key = `${d.group}.${d.binding}`;
      const prev = decls.get(key);
      if (prev) prev.stages |= bit; else decls.set(key, { ...d, stages: bit });
    }
  }
  if (errors.length) return [];

  // Resources.
  const byKey = new Map(job.resources.map((r) => [`${r.set}.${r.binding}`, r]));
  const encoder = device.createCommandEncoder();
  const objects = new Map();
  const readbacks = [];
  const staged = [];
  for (const [key, decl] of decls) {
    const res = byKey.get(key);
    if (!res) { errors.push(`the shader declares ${decl.name} at group/binding ${key}, which the case does not provide`); continue; }
    if (res.kind === 'uniform') {
      objects.set(key, { buffer: makeBuffer(GPUBufferUsage.UNIFORM, fromBase64(res.data), minBindingSize(decl)) });
    } else if (res.kind === 'storage') {
      const bytes = fromBase64(res.data);
      const buffer = makeBuffer(GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC, bytes);
      objects.set(key, { buffer });
    } else if (res.kind === 'texture') {
      const depth = res.texture.format === 'depth32float';
      const texture = makeTexture(res, GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | (depth ? GPUTextureUsage.RENDER_ATTACHMENT : 0));
      if (depth) uploadDepthTexture(res, texture, fromBase64(res.data), encoder); else uploadTexture(res, texture, fromBase64(res.data));
      const dim = res.texture.cube ? 'cube' : res.texture.layers > 0 ? '2d-array' : '2d';
      objects.set(key, { view: texture.createView({ dimension: dim }) });
    } else if (res.kind === 'storageimage') {
      const texture = makeTexture(res, GPUTextureUsage.STORAGE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.COPY_SRC);
      uploadTexture(res, texture, fromBase64(res.data));
      objects.set(key, { view: texture.createView({ dimension: res.texture.layers > 0 ? '2d-array' : '2d' }), texture });
    } else if (res.kind === 'sampler') {
      const s = res.sampler;
      const mode = s.repeat ? 'repeat' : 'clamp-to-edge';
      const filter = s.linear ? 'linear' : 'nearest';
      const desc = {
        magFilter: filter, minFilter: filter, mipmapFilter: s.mipFilter ? 'linear' : 'nearest',
        addressModeU: mode, addressModeV: mode, addressModeW: mode,
        lodMinClamp: 0, lodMaxClamp: s.mipFilter ? 16 : 0,
      };
      if (s.compare) desc.compare = 'less-equal';
      objects.set(key, { sampler: device.createSampler(desc) });
    }
  }
  if (errors.length) return [];

  // Layouts: one bind group per group index, built from what the shaders declare.
  const maxGroup = Math.max(-1, ...[...decls.values()].map((d) => d.group));
  const groupLayouts = [];
  const groups = [];
  for (let g = 0; g <= maxGroup; g++) {
    const layoutEntries = [];
    const entries = [];
    for (const [key, decl] of decls) {
      if (decl.group !== g) continue;
      const res = byKey.get(key);
      const vis = job.kind === 'compute' ? GPUShaderStage.COMPUTE : decl.stages;
      layoutEntries.push(layoutEntryFor(decl, res, vis));
      const o = objects.get(key);
      const resource = o.buffer ? { buffer: o.buffer } : o.view ?? o.sampler;
      entries.push({ binding: decl.binding, resource });
    }
    const layout = device.createBindGroupLayout({ entries: layoutEntries });
    groupLayouts.push(layout);
    groups.push(device.createBindGroup({ layout, entries }));
  }
  const pipelineLayout = device.createPipelineLayout({ bindGroupLayouts: groupLayouts });
  const bind = (pass) => groups.forEach((bg, i) => pass.setBindGroup(i, bg));

  const outputs = [];
  if (job.kind === 'compute') {
    const pipelines = new Map();
    for (const d of job.dispatches) {
      if (!pipelines.has(d.entry)) {
        pipelines.set(d.entry, device.createComputePipeline({ layout: pipelineLayout, compute: { module: modules[d.entry], entryPoint: d.entry } }));
      }
    }
    for (const d of job.dispatches) {
      const pass = encoder.beginComputePass();
      pass.setPipeline(pipelines.get(d.entry));
      bind(pass);
      pass.dispatchWorkgroups(d.x, d.y, d.z);
      pass.end();
    }
    for (const res of job.resources) {
      if (!res.output) continue;
      const o = objects.get(`${res.set}.${res.binding}`);
      if (!o) { errors.push(`output ${res.set}.${res.binding} is not used by the shader`); continue; }
      if (res.kind === 'storage') {
        const size = align(Math.max(fromBase64(res.data).length, 4), 16);
        const buffer = device.createBuffer({ size, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ });
        encoder.copyBufferToBuffer(o.buffer, 0, buffer, 0, size);
        staged.push({ key: res.key, buffer, length: res.length });
      } else if (res.kind === 'storageimage') {
        const t = res.texture;
        const layers = Math.max(t.layers, 1);
        readbacks.push({ key: res.key, width: t.width, height: t.height * layers, rb: copyTextureOut(encoder, o.texture, t.width, t.height, layers, BYTES_PER_TEXEL[t.format]) });
      }
    }
  } else {
    const g = job.graphics;
    const targetBpt = BYTES_PER_TEXEL[g.targetFormat];
    const target = device.createTexture({
      size: [g.width, g.height], format: g.targetFormat,
      usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_DST | GPUTextureUsage.COPY_SRC,
    });
    device.queue.writeTexture({ texture: target }, fromBase64(g.background),
      { bytesPerRow: g.width * targetBpt, rowsPerImage: g.height }, [g.width, g.height]);
    const depth = g.depth ? device.createTexture({
      size: [g.width, g.height], format: 'depth32float', usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC,
    }) : null;

    const vbLayouts = [];
    const vbBuffers = [];
    for (const vb of g.vertexBuffers) {
      vbLayouts[vb.binding] = {
        arrayStride: vb.stride, stepMode: vb.perInstance ? 'instance' : 'vertex',
        attributes: g.attributes.filter((a) => a.binding === vb.binding).map((a) => ({ shaderLocation: a.location, offset: a.offset, format: VERTEX_FORMATS[a.format] })),
      };
      vbBuffers[vb.binding] = makeBuffer(GPUBufferUsage.VERTEX, fromBase64(vb.data));
    }
    for (let i = 0; i < vbLayouts.length; i++) vbLayouts[i] ??= null;
    const indexBuffer = g.index ? makeBuffer(GPUBufferUsage.INDEX, fromBase64(g.index.data)) : null;

    const desc = {
      layout: pipelineLayout,
      vertex: { module: modules[g.vertexEntry], entryPoint: g.vertexEntry, buffers: vbLayouts },
      primitive: { topology: 'triangle-list', frontFace: 'ccw', cullMode: { none: 'none', back: 'back', front: 'front' }[g.cull] },
    };
    if (g.pixelEntry) {
      const t = { format: g.targetFormat };
      const b = blendState(g.blend);
      if (b) t.blend = b;
      desc.fragment = { module: modules[g.pixelEntry], entryPoint: g.pixelEntry, targets: [t] };
    }
    if (depth) desc.depthStencil = { format: 'depth32float', depthWriteEnabled: true, depthCompare: g.depth.test ? 'less' : 'always' };
    const pipeline = device.createRenderPipeline(desc);

    const pass = encoder.beginRenderPass({
      colorAttachments: g.pixelEntry ? [{ view: target.createView(), loadOp: 'load', storeOp: 'store' }] : [],
      ...(depth ? { depthStencilAttachment: { view: depth.createView(), depthClearValue: g.depth.clear, depthLoadOp: 'clear', depthStoreOp: 'store' } } : {}),
    });
    pass.setPipeline(pipeline);
    bind(pass);
    vbBuffers.forEach((b, i) => { if (b) pass.setVertexBuffer(i, b); });
    if (indexBuffer) {
      pass.setIndexBuffer(indexBuffer, g.index.is16 ? 'uint16' : 'uint32');
      pass.drawIndexed(g.count, g.instances);
    } else {
      pass.draw(g.count, g.instances);
    }
    pass.end();
    readbacks.push({ key: 'target', width: g.width, height: g.height, rb: copyTextureOut(encoder, target, g.width, g.height, 1, targetBpt) });
    if (depth && g.depth.output) {
      readbacks.push({ key: 'depth', width: g.width, height: g.height, rb: copyTextureOut(encoder, depth, g.width, g.height, 1, 4, 'depth-only') });
    }
  }

  device.queue.submit([encoder.finish()]);
  const outs = [];
  for (const rb of readbacks) {
    outs.push({ key: rb.key, width: rb.width, height: rb.height, data: toBase64(await readTexture(rb.rb)) });
  }
  for (const s of staged) {
    await s.buffer.mapAsync(GPUMapMode.READ);
    const bytes = new Uint8Array(s.buffer.getMappedRange()).slice(0, s.length);
    s.buffer.unmap();
    outs.push({ key: s.key, width: 0, height: 0, data: toBase64(bytes) });
  }
  return outs;
}

window.runJob = async (job) => {
  await init();
  const errors = [];
  sink = errors;
  const scopes = ['validation', 'out-of-memory', 'internal'];
  scopes.forEach((s) => device.pushErrorScope(s));
  let outputs = [];
  try {
    outputs = await Promise.race([
      execute(job, errors),
      new Promise((_, reject) => setTimeout(() => reject(new Error('timed out after 60 s')), 60000)),
    ]);
    await device.queue.onSubmittedWorkDone();
  } catch (e) {
    errors.push('exception: ' + (e && e.message ? e.message : e));
  }
  for (const s of scopes.slice().reverse()) {
    const err = await device.popErrorScope();
    if (err) errors.push(`${s}: ${err.message}`);
  }
  sink = null;
  return { name: job.name, outputs, errors };
};
