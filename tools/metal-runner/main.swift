// Executes one exported parity case on the system's default Metal device.
//
//   metal-runner <case-directory>
//
// The directory comes from the parity harness (tests/OneShader.ParityTests, MetalExportTests): a plain-text
// manifest `case.txt`, the MSL sources, and the raw input bytes. Outputs are written to `<case-directory>/metal/`.
// This is test tooling only; nothing here ships.
//
// Exit codes: 0 done; 3 a shader failed to compile; 4 a Metal error while running; 2 bad input.
// Everything the runner says goes to stdout; stderr is left to the Metal runtime so that validation
// output (MTL_DEBUG_LAYER, MTL_SHADER_VALIDATION) can be told apart from the runner's own messages.
//
// Y convention: Vulkan clip space has +Y down and the shaders are written for it. Metal has +Y up. The
// draw uses a viewport with a negative height, which flips the mapping so the same vertex positions land
// on the same pixels. Depth range (0..1) and the texture origin (top left) already agree.

import Foundation
import Metal

setvbuf(stdout, nil, _IOLBF, 0)

func die(_ code: Int32, _ message: String) -> Never {
    print("runner: " + message)
    exit(code)
}

guard CommandLine.arguments.count == 2 else { die(2, "usage: metal-runner <case-directory>") }
let caseDir = URL(fileURLWithPath: CommandLine.arguments[1])
let outDir = caseDir.appendingPathComponent("metal")
try? FileManager.default.removeItem(at: outDir)
try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)

guard let device = MTLCreateSystemDefaultDevice() else { die(2, "no Metal device") }
guard let queue = device.makeCommandQueue() else { die(2, "no command queue") }

func readFile(_ name: String) -> Data {
    do { return try Data(contentsOf: caseDir.appendingPathComponent(name)) } catch { die(2, "cannot read \(name)") }
}

func writeOut(_ name: String, _ data: Data) {
    do { try data.write(to: outDir.appendingPathComponent(name)) } catch { die(2, "cannot write \(name)") }
}

func pixelFormat(_ s: String) -> MTLPixelFormat {
    switch s {
    case "r8": return .r8Unorm
    case "rgba8": return .rgba8Unorm
    case "r32f": return .r32Float
    case "rgba32f": return .rgba32Float
    case "r32uint": return .r32Uint
    case "rgba16f": return .rgba16Float
    case "depth32f": return .depth32Float
    default: die(2, "unknown pixel format \(s)")
    }
}

func bytesPerTexel(_ s: String) -> Int {
    switch s {
    case "r8": return 1
    case "rgba8", "r32f", "r32uint", "depth32f": return 4
    case "rgba16f": return 8
    case "rgba32f": return 16
    default: die(2, "unknown pixel format \(s)")
    }
}

func vertexFormat(_ s: String) -> MTLVertexFormat {
    switch s {
    case "float1": return .float
    case "float2": return .float2
    case "float3": return .float3
    case "float4": return .float4
    case "unorm8x4": return .uchar4Normalized
    case "word": return .uint
    case "uint4": return .uint4
    default: die(2, "unknown vertex format \(s)")
    }
}

func textureType(_ s: String) -> MTLTextureType {
    switch s {
    case "2d": return .type2D
    case "2darray": return .type2DArray
    case "cube": return .typeCube
    default: die(2, "unknown texture type \(s)")
    }
}

let mathMode: MTLMathMode = ProcessInfo.processInfo.environment["ONESHADER_METAL_MATH"] == "safe" ? .safe : .fast

var libraries: [String: MTLLibrary] = [:]
func function(file: String, name: String) -> MTLFunction {
    let library: MTLLibrary
    if let cached = libraries[file] {
        library = cached
    } else {
        let source = String(decoding: readFile(file), as: UTF8.self)
        let options = MTLCompileOptions()
        options.languageVersion = .version2_4
        options.mathMode = mathMode
        // Turns on the static_asserts that compare every buffer struct with the reflected layout.
        options.preprocessorMacros = ["ONESHADER_CHECK_LAYOUT": NSNumber(value: 1)]
        do { library = try device.makeLibrary(source: source, options: options) } catch {
            die(3, "\(file) did not compile: \(error)")
        }
        libraries[file] = library
    }
    guard let f = library.makeFunction(name: name) else { die(3, "\(file) has no function \(name)") }
    return f
}

// --- manifest -------------------------------------------------------------------------------------------

let manifest = String(decoding: readFile("case.txt"), as: UTF8.self)
    .split(separator: "\n").map { $0.split(separator: " ").map(String.init) }.filter { !$0.isEmpty }

var buffers: [(index: Int, buffer: MTLBuffer, out: String)] = []
var textures: [(index: Int, texture: MTLTexture)] = []
var samplers: [(index: Int, state: MTLSamplerState)] = []
// Texture readbacks: texture, width, height, layers, bytes per texel, output file.
var imageOutputs: [(texture: MTLTexture, w: Int, h: Int, layers: Int, bpt: Int, out: String)] = []
var uploads: [(texture: MTLTexture, w: Int, h: Int, layers: Int, mips: Int, bpt: Int, data: Data, depth: Bool)] = []

func makeBuffer(_ data: Data) -> MTLBuffer {
    let length = max(data.count, 16)
    guard let b = device.makeBuffer(length: length, options: .storageModeShared) else { die(4, "buffer allocation failed") }
    data.withUnsafeBytes { raw in
        if raw.count > 0 { b.contents().copyMemory(from: raw.baseAddress!, byteCount: raw.count) }
    }
    return b
}

func makeTexture(type: String, w: Int, h: Int, layers: Int, mips: Int, format: String, usage: MTLTextureUsage) -> MTLTexture {
    let d = MTLTextureDescriptor()
    d.textureType = textureType(type)
    d.pixelFormat = pixelFormat(format)
    d.width = w
    d.height = h
    d.mipmapLevelCount = mips
    d.arrayLength = type == "2darray" ? max(layers, 1) : 1
    d.usage = usage
    d.storageMode = .private
    guard let t = device.makeTexture(descriptor: d) else { die(4, "texture allocation failed") }
    return t
}

var dispatches: [(file: String, fn: String, grid: [Int], group: [Int])] = []
var graphics: [String]? = nil
var depth: (clear: Double, test: Bool, out: String)? = nil
var vertexBuffers: [(index: Int, stride: Int, perInstance: Bool, buffer: MTLBuffer)] = []
var attributes: [(loc: Int, format: String, offset: Int, buffer: Int)] = []
var indexBuffer: (is16: Bool, buffer: MTLBuffer)? = nil
var background: Data? = nil
var targetOut = ""

for t in manifest {
    switch t[0] {
    case "buffer":
        buffers.append((Int(t[1])!, makeBuffer(readFile(t[2])), t[3] == "-" ? "" : t[3]))
    case "texture":
        // texture INDEX TYPE W H LAYERS MIPS FORMAT FILE
        let (w, h, layers, mips) = (Int(t[3])!, Int(t[4])!, Int(t[5])!, Int(t[6])!)
        let tex = makeTexture(type: t[2], w: w, h: h, layers: layers, mips: mips, format: t[7], usage: .shaderRead)
        textures.append((Int(t[1])!, tex))
        uploads.append((tex, w, h, layers, mips, bytesPerTexel(t[7]), readFile(t[8]), t[7] == "depth32f"))
    case "image":
        // image INDEX TYPE W H LAYERS FORMAT FILE OUT
        let (w, h, layers) = (Int(t[3])!, Int(t[4])!, Int(t[5])!)
        let tex = makeTexture(type: t[2], w: w, h: h, layers: layers, mips: 1, format: t[6], usage: [.shaderRead, .shaderWrite])
        textures.append((Int(t[1])!, tex))
        uploads.append((tex, w, h, layers, 1, bytesPerTexel(t[6]), readFile(t[7]), false))
        if t[8] != "-" { imageOutputs.append((tex, w, h, max(layers, 1), bytesPerTexel(t[6]), t[8])) }
    case "sampler":
        // sampler INDEX linear|nearest repeat|clamp MIPS COMPARE
        let d = MTLSamplerDescriptor()
        let filter: MTLSamplerMinMagFilter = t[2] == "linear" ? .linear : .nearest
        d.minFilter = filter
        d.magFilter = filter
        d.mipFilter = t[4] == "1" ? .linear : .notMipmapped
        let mode: MTLSamplerAddressMode = t[3] == "repeat" ? .repeat : .clampToEdge
        d.sAddressMode = mode
        d.tAddressMode = mode
        d.rAddressMode = mode
        if t[5] == "1" { d.compareFunction = .lessEqual }
        guard let s = device.makeSamplerState(descriptor: d) else { die(4, "sampler creation failed") }
        samplers.append((Int(t[1])!, s))
    case "compute":
        // compute FILE FUNCTION GX GY GZ TX TY TZ
        dispatches.append((t[1], t[2], [Int(t[3])!, Int(t[4])!, Int(t[5])!], [Int(t[6])!, Int(t[7])!, Int(t[8])!]))
    case "graphics":
        // graphics VSFILE VSFUNC PSFILE PSFUNC W H FORMAT BLEND CULL INSTANCES COUNT INDEXED
        graphics = t
    case "depth":
        // depth CLEAR TEST OUT
        depth = (Double(t[1])!, t[2] == "1", t[3] == "-" ? "" : t[3])
    case "vbuf":
        // vbuf INDEX STRIDE PERINSTANCE FILE
        vertexBuffers.append((Int(t[1])!, Int(t[2])!, t[3] == "1", makeBuffer(readFile(t[4]))))
    case "attr":
        attributes.append((Int(t[1])!, t[2], Int(t[3])!, Int(t[4])!))
    case "index":
        indexBuffer = (t[1] == "16", makeBuffer(readFile(t[2])))
    case "background":
        background = readFile(t[1])
    case "targetout":
        targetOut = t[1]
    default:
        die(2, "unknown manifest line \(t[0])")
    }
}

let descriptor = MTLCommandBufferDescriptor()
descriptor.errorOptions = .encoderExecutionStatus
guard let commands = queue.makeCommandBuffer(descriptor: descriptor) else { die(4, "no command buffer") }

// --- uploads ----------------------------------------------------------------------------------------------

func upload(_ blit: MTLBlitCommandEncoder, _ tex: MTLTexture, w: Int, h: Int, layers: Int, mips: Int, bpt: Int, data: Data, depth: Bool) {
    let staging = makeBuffer(data)
    let slices = max(layers, 1)
    var offset = 0
    for level in 0..<mips {
        let lw = max(w >> level, 1), lh = max(h >> level, 1)
        for slice in 0..<slices {
            blit.copy(from: staging, sourceOffset: offset, sourceBytesPerRow: lw * bpt, sourceBytesPerImage: lw * lh * bpt,
                      sourceSize: MTLSize(width: lw, height: lh, depth: 1), to: tex, destinationSlice: slice,
                      destinationLevel: level, destinationOrigin: MTLOrigin(x: 0, y: 0, z: 0))
            offset += lw * lh * bpt
        }
    }
}

guard let setup = commands.makeBlitCommandEncoder() else { die(4, "no blit encoder") }
for u in uploads { upload(setup, u.texture, w: u.w, h: u.h, layers: u.layers, mips: u.mips, bpt: u.bpt, data: u.data, depth: u.depth) }

var targetTexture: MTLTexture? = nil
var depthTexture: MTLTexture? = nil
if let g = graphics {
    let (w, h, format) = (Int(g[5])!, Int(g[6])!, g[7])
    targetTexture = makeTexture(type: "2d", w: w, h: h, layers: 1, mips: 1, format: format, usage: [.renderTarget, .shaderRead])
    upload(setup, targetTexture!, w: w, h: h, layers: 1, mips: 1, bpt: bytesPerTexel(format), data: background ?? Data(count: w * h * bytesPerTexel(format)), depth: false)
    if depth != nil {
        depthTexture = makeTexture(type: "2d", w: w, h: h, layers: 1, mips: 1, format: "depth32f", usage: [.renderTarget, .shaderRead])
    }
}
setup.endEncoding()

// --- bindings -----------------------------------------------------------------------------------------------

func bindCompute(_ e: MTLComputeCommandEncoder) {
    for b in buffers { e.setBuffer(b.buffer, offset: 0, index: b.index) }
    for t in textures { e.setTexture(t.texture, index: t.index) }
    for s in samplers { e.setSamplerState(s.state, index: s.index) }
}

func bindRender(_ e: MTLRenderCommandEncoder) {
    for b in buffers {
        e.setVertexBuffer(b.buffer, offset: 0, index: b.index)
        e.setFragmentBuffer(b.buffer, offset: 0, index: b.index)
    }
    for t in textures {
        e.setVertexTexture(t.texture, index: t.index)
        e.setFragmentTexture(t.texture, index: t.index)
    }
    for s in samplers {
        e.setVertexSamplerState(s.state, index: s.index)
        e.setFragmentSamplerState(s.state, index: s.index)
    }
}

// --- compute ------------------------------------------------------------------------------------------------

for d in dispatches {
    let pipeline: MTLComputePipelineState
    do { pipeline = try device.makeComputePipelineState(function: function(file: d.file, name: d.fn)) } catch {
        die(3, "compute pipeline for \(d.fn) failed: \(error)")
    }
    if d.group[0] * d.group[1] * d.group[2] > pipeline.maxTotalThreadsPerThreadgroup {
        die(4, "\(d.fn): threadgroup \(d.group) exceeds the device limit \(pipeline.maxTotalThreadsPerThreadgroup)")
    }
    guard let e = commands.makeComputeCommandEncoder() else { die(4, "no compute encoder") }
    e.setComputePipelineState(pipeline)
    bindCompute(e)
    e.dispatchThreadgroups(MTLSize(width: d.grid[0], height: d.grid[1], depth: d.grid[2]),
                           threadsPerThreadgroup: MTLSize(width: d.group[0], height: d.group[1], depth: d.group[2]))
    e.endEncoding()
}

// --- graphics -----------------------------------------------------------------------------------------------

if let g = graphics, let target = targetTexture {
    let w = Int(g[5])!, h = Int(g[6])!
    let desc = MTLRenderPipelineDescriptor()
    desc.vertexFunction = function(file: g[1], name: g[2])
    if g[3] != "-" { desc.fragmentFunction = function(file: g[3], name: g[4]) }
    desc.colorAttachments[0].pixelFormat = target.pixelFormat
    if depth != nil { desc.depthAttachmentPixelFormat = .depth32Float }
    switch g[8] {
    case "premul", "alpha":
        let a = desc.colorAttachments[0]!
        a.isBlendingEnabled = true
        a.sourceRGBBlendFactor = g[8] == "alpha" ? .sourceAlpha : .one
        a.destinationRGBBlendFactor = .oneMinusSourceAlpha
        a.rgbBlendOperation = .add
        a.sourceAlphaBlendFactor = .one
        a.destinationAlphaBlendFactor = .oneMinusSourceAlpha
        a.alphaBlendOperation = .add
    default: break
    }
    if !attributes.isEmpty {
        // Shader inputs are [[attribute(location)]]; the manifest names the Metal buffer index of every vertex buffer.
        let vd = MTLVertexDescriptor()
        for a in attributes {
            vd.attributes[a.loc].format = vertexFormat(a.format)
            vd.attributes[a.loc].offset = a.offset
            vd.attributes[a.loc].bufferIndex = a.buffer
        }
        for v in vertexBuffers {
            vd.layouts[v.index].stride = v.stride
            vd.layouts[v.index].stepFunction = v.perInstance ? .perInstance : .perVertex
            vd.layouts[v.index].stepRate = 1
        }
        desc.vertexDescriptor = vd
    }
    let pipeline: MTLRenderPipelineState
    do { pipeline = try device.makeRenderPipelineState(descriptor: desc) } catch { die(3, "render pipeline failed: \(error)") }

    let pass = MTLRenderPassDescriptor()
    pass.colorAttachments[0].texture = target
    pass.colorAttachments[0].loadAction = .load
    pass.colorAttachments[0].storeAction = .store
    var depthState: MTLDepthStencilState? = nil
    if let d = depth, let dt = depthTexture {
        pass.depthAttachment.texture = dt
        pass.depthAttachment.loadAction = .clear
        pass.depthAttachment.clearDepth = d.clear
        pass.depthAttachment.storeAction = .store
        let ds = MTLDepthStencilDescriptor()
        ds.depthCompareFunction = d.test ? .less : .always
        ds.isDepthWriteEnabled = true
        depthState = device.makeDepthStencilState(descriptor: ds)
    }
    guard let e = commands.makeRenderCommandEncoder(descriptor: pass) else { die(4, "no render encoder") }
    e.setRenderPipelineState(pipeline)
    if let ds = depthState { e.setDepthStencilState(ds) }
    e.setViewport(MTLViewport(originX: 0, originY: Double(h), width: Double(w), height: -Double(h), znear: 0, zfar: 1))
    // Vulkan's counter-clockwise front face, seen through the flipped viewport.
    e.setFrontFacing(ProcessInfo.processInfo.environment["ONESHADER_METAL_WINDING"] == "ccw" ? .counterClockwise : .clockwise)
    switch g[9] {
    case "back": e.setCullMode(.back)
    case "front": e.setCullMode(.front)
    default: e.setCullMode(.none)
    }
    bindRender(e)
    for v in vertexBuffers { e.setVertexBuffer(v.buffer, offset: 0, index: v.index) }
    let instances = Int(g[10])!, count = Int(g[11])!
    if g[12] == "1", let ib = indexBuffer {
        e.drawIndexedPrimitives(type: .triangle, indexCount: count, indexType: ib.is16 ? .uint16 : .uint32,
                                indexBuffer: ib.buffer, indexBufferOffset: 0, instanceCount: instances)
    } else {
        e.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: count, instanceCount: instances)
    }
    e.endEncoding()
}

// --- readbacks --------------------------------------------------------------------------------------------

var readbacks: [(buffer: MTLBuffer, out: String)] = []
if let blit = commands.makeBlitCommandEncoder() {
    func read(_ tex: MTLTexture, w: Int, h: Int, layers: Int, bpt: Int, out: String) {
        let size = w * h * layers * bpt
        guard let b = device.makeBuffer(length: size, options: .storageModeShared) else { die(4, "readback allocation failed") }
        for slice in 0..<layers {
            blit.copy(from: tex, sourceSlice: slice, sourceLevel: 0, sourceOrigin: MTLOrigin(x: 0, y: 0, z: 0),
                      sourceSize: MTLSize(width: w, height: h, depth: 1), to: b, destinationOffset: slice * w * h * bpt,
                      destinationBytesPerRow: w * bpt, destinationBytesPerImage: w * h * bpt)
        }
        readbacks.append((b, out))
    }
    if let g = graphics, let target = targetTexture {
        read(target, w: Int(g[5])!, h: Int(g[6])!, layers: 1, bpt: bytesPerTexel(g[7]), out: targetOut)
        if let d = depth, !d.out.isEmpty, let dt = depthTexture {
            read(dt, w: Int(g[5])!, h: Int(g[6])!, layers: 1, bpt: 4, out: d.out)
        }
    }
    for o in imageOutputs { read(o.texture, w: o.w, h: o.h, layers: o.layers, bpt: o.bpt, out: o.out) }
    blit.endEncoding()
}

commands.commit()
commands.waitUntilCompleted()
if commands.status != .completed {
    die(4, "command buffer \(commands.status.rawValue): \(String(describing: commands.error))")
}

for b in buffers where !b.out.isEmpty {
    writeOut(b.out, Data(bytes: b.buffer.contents(), count: b.buffer.length))
}
for r in readbacks {
    writeOut(r.out, Data(bytes: r.buffer.contents(), count: r.buffer.length))
}
print("runner: ok")
