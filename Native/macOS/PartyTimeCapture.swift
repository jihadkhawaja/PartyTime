import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreVideo
import AVFoundation
import Darwin

// stdout is a binary pipe: interleaved float32 stereo at 48 kHz, or tightly packed BGRA.
// All diagnostics go to stderr. No audio or screenshots are written to disk.
@available(macOS 13.0, *)
final class Capture: NSObject, SCStreamOutput, SCStreamDelegate {
    var stream: SCStream?
    var mode = "audio"
    var width = 2
    var height = 2
    var parentWatch: DispatchSourceTimer?
    let output = FileHandle.standardOutput
    var videoFrame = Data()

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        fail(error.localizedDescription)
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sample: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sample.isValid else { return }
        if type == .audio && mode == "audio" {
            guard let description = sample.formatDescription,
                  let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(description),
                  asbd.pointee.mFormatID == kAudioFormatLinearPCM,
                  asbd.pointee.mFormatFlags & kAudioFormatFlagIsFloat != 0,
                  asbd.pointee.mBitsPerChannel == 32,
                  asbd.pointee.mSampleRate == 48000,
                  asbd.pointee.mChannelsPerFrame == 2 else {
                fail("ScreenCaptureKit returned an unsupported audio format.")
            }
            var size = 0
            var block: CMBlockBuffer?
            CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sample, bufferListSizeNeededOut: &size,
                bufferListOut: nil, bufferListSize: 0, blockBufferAllocator: nil, blockBufferMemoryAllocator: nil,
                flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment), blockBufferOut: &block)
            let storage = UnsafeMutableRawPointer.allocate(byteCount: size, alignment: 16)
            defer { storage.deallocate() }
            let list = storage.bindMemory(to: AudioBufferList.self, capacity: 1)
            guard CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sample, bufferListSizeNeededOut: nil,
                bufferListOut: list, bufferListSize: size, blockBufferAllocator: nil, blockBufferMemoryAllocator: nil,
                flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment), blockBufferOut: &block) == noErr else { return }
            let buffers = UnsafeMutableAudioBufferListPointer(list)
            let frames = sample.numSamples
            if buffers.count == 1, let data = buffers[0].mData {
                output.write(Data(bytes: data, count: frames * 8))
            } else if buffers.count == 2, let l = buffers[0].mData, let r = buffers[1].mData {
                let left = l.assumingMemoryBound(to: Float.self)
                let right = r.assumingMemoryBound(to: Float.self)
                var interleaved = [Float](repeating: 0, count: frames * 2)
                for i in 0..<frames { interleaved[i * 2] = left[i]; interleaved[i * 2 + 1] = right[i] }
                interleaved.withUnsafeBytes { output.write(Data($0)) }
            }
        } else if type == .screen && mode == "screen", let image = sample.imageBuffer {
            guard CVPixelBufferGetWidth(image) == width && CVPixelBufferGetHeight(image) == height else { return }
            CVPixelBufferLockBaseAddress(image, .readOnly)
            defer { CVPixelBufferUnlockBaseAddress(image, .readOnly) }
            guard let base = CVPixelBufferGetBaseAddress(image) else { return }
            let stride = CVPixelBufferGetBytesPerRow(image)
            if videoFrame.count != width * height * 4 { videoFrame = Data(count: width * height * 4) }
            videoFrame.withUnsafeMutableBytes { destination in
                guard let target = destination.baseAddress else { return }
                for y in 0..<height { memcpy(target.advanced(by: y * width * 4), base.advanced(by: y * stride), width * 4) }
            }
            output.write(videoFrame)
        }
    }

    func start() async throws {
        let args = CommandLine.arguments
        mode = args[1]
        let parent = pid_t(args.last!)!
        // Exit if the owning app disappears, including crashes or forced termination.
        let watch = DispatchSource.makeTimerSource(queue: DispatchQueue.global())
        watch.schedule(deadline: .now() + 1, repeating: 1)
        watch.setEventHandler { if kill(parent, 0) != 0 { exit(0) } }
        parentWatch = watch
        watch.resume()
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard var display = content.displays.first else { throw NSError(domain: "PartyTime", code: 1, userInfo: [NSLocalizedDescriptionKey: "No display is available for capture."]) }
        if mode == "screen" {
            let id = UInt32(args[2]) ?? 0
            let x = Double(args[3]) ?? 0, y = Double(args[4]) ?? 0
            display = content.displays.first(where: { $0.displayID == id })
                ?? content.displays.min(by: { hypot($0.frame.minX - x, $0.frame.minY - y) < hypot($1.frame.minX - x, $1.frame.minY - y) })!
            width = Int(args[5])!; height = Int(args[6])!
        }
        let excluded = content.applications.filter { $0.processID == parent || $0.processID == getpid() }
        let filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])
        let configuration = SCStreamConfiguration()
        configuration.width = width; configuration.height = height
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        let frameRate = mode == "screen" && args.count >= 9 ? (Int32(args[7]) ?? 60) : 30
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: mode == "audio" ? 1 : max(1, min(60, frameRate)))
        configuration.showsCursor = false
        configuration.capturesAudio = mode == "audio"
        configuration.sampleRate = 48000; configuration.channelCount = 2
        configuration.excludesCurrentProcessAudio = true
        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        self.stream = stream
        try stream.addStreamOutput(self, type: mode == "audio" ? .audio : .screen, sampleHandlerQueue: DispatchQueue(label: "PartyTime.capture"))
        try await stream.startCapture()
    }
}

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\nAllow PartyTime in System Settings → Privacy & Security → Screen & System Audio Recording.\n").utf8))
    exit(1)
}

guard CommandLine.arguments.count >= 3 else { fail("Missing capture arguments.") }
if #available(macOS 13.0, *) {
    let capture = Capture()
    Task { do { try await capture.start() } catch { fail(error.localizedDescription) } }
    withExtendedLifetime(capture) { RunLoop.main.run() }
} else { fail("System audio capture requires macOS 13 or later.") }
