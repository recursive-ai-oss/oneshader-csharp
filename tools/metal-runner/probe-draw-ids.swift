// Answers one question about Metal's draw-id semantics: do [[vertex_id]] and [[instance_id]] already include the
// draw's base vertex / base instance, or must a shader add [[base_vertex]] / [[base_instance]] to match Vulkan's
// gl_VertexIndex / gl_InstanceIndex? Runs four draws with non-zero bases and prints what each shader invocation saw.
//
//   xcrun swiftc -swift-version 5 probe-draw-ids.swift -o probe-draw-ids && ./probe-draw-ids

import Foundation
import Metal

let source = """
#include <metal_stdlib>
using namespace metal;
struct Log { atomic_uint count; uint rows[4 * 32]; };
vertex void probe(uint vid [[vertex_id]], uint bv [[base_vertex]], uint iid [[instance_id]], uint bi [[base_instance]],
                    device Log& log [[buffer(0)]])
{
    uint slot = atomic_fetch_add_explicit(&log.count, 1u, memory_order_relaxed);
    log.rows[slot * 4 + 0] = vid;
    log.rows[slot * 4 + 1] = bv;
    log.rows[slot * 4 + 2] = iid;
    log.rows[slot * 4 + 3] = bi;
}
"""

let device = MTLCreateSystemDefaultDevice()!
let queue = device.makeCommandQueue()!
let library = try! device.makeLibrary(source: source, options: nil)
let desc = MTLRenderPipelineDescriptor()
desc.vertexFunction = library.makeFunction(name: "probe")
desc.isRasterizationEnabled = false
let pipeline = try! device.makeRenderPipelineState(descriptor: desc)

let targetDesc = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm, width: 1, height: 1, mipmapped: false)
targetDesc.usage = .renderTarget
targetDesc.storageMode = .private
let target = device.makeTexture(descriptor: targetDesc)!
let indices: [UInt32] = [0, 1, 2]
let indexBuffer = device.makeBuffer(bytes: indices, length: 12, options: .storageModeShared)!

func run(_ title: String, _ draw: (MTLRenderCommandEncoder) -> Void) {
    let log = device.makeBuffer(length: 4 + 4 * 32 * 4, options: .storageModeShared)!
    memset(log.contents(), 0, log.length)
    let pass = MTLRenderPassDescriptor()
    pass.colorAttachments[0].texture = target
    pass.colorAttachments[0].loadAction = .dontCare
    pass.colorAttachments[0].storeAction = .dontCare
    let cb = queue.makeCommandBuffer()!
    let e = cb.makeRenderCommandEncoder(descriptor: pass)!
    e.setRenderPipelineState(pipeline)
    e.setVertexBuffer(log, offset: 0, index: 0)
    draw(e)
    e.endEncoding()
    cb.commit()
    cb.waitUntilCompleted()
    let words = log.contents().bindMemory(to: UInt32.self, capacity: 1 + 4 * 32)
    var rows: [[UInt32]] = []
    for i in 0..<Int(words[0]) { rows.append((0..<4).map { words[1 + i * 4 + $0] }) }
    rows.sort { $0.lexicographicallyPrecedes($1) }
    print(title)
    print("  (vertex_id, base_vertex, instance_id, base_instance): \(rows.map { "(\($0[0]),\($0[1]),\($0[2]),\($0[3]))" }.joined(separator: " "))")
}

run("drawPrimitives vertexStart 5, 3 vertices, 2 instances, baseInstance 7") {
    $0.drawPrimitives(type: .triangle, vertexStart: 5, vertexCount: 3, instanceCount: 2, baseInstance: 7)
}
run("drawIndexedPrimitives indices 0,1,2, baseVertex 10, 2 instances, baseInstance 7") {
    $0.drawIndexedPrimitives(type: .triangle, indexCount: 3, indexType: .uint32, indexBuffer: indexBuffer, indexBufferOffset: 0,
                             instanceCount: 2, baseVertex: 10, baseInstance: 7)
}
