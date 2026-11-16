// Draws the app icon (a budget gauge on a warm rounded square) and writes an .iconset folder.
// Usage: swift scripts/make-icon.swift <output.iconset>
// Normally run via scripts/make-icon.sh, which also converts the iconset to Resources/AppIcon.icns.
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

func color(_ hex: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(
        srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
        green: CGFloat((hex >> 8) & 0xFF) / 255,
        blue: CGFloat(hex & 0xFF) / 255,
        alpha: alpha
    )
}

/// Draws the icon in a 1024×1024 coordinate space (origin bottom-left); the context is pre-scaled.
func drawIcon(in ctx: CGContext) {
    // macOS icon grid: an 824pt body centred in a 1024pt canvas, leaving room for the shadow.
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let shape = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)

    // Soft drop shadow under the body.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: color(0x000000, 0.35))
    ctx.addPath(shape)
    ctx.setFillColor(color(0xC8603A))
    ctx.fillPath()
    ctx.restoreGState()

    // Body: warm vertical gradient.
    ctx.saveGState()
    ctx.addPath(shape)
    ctx.clip()
    let gradient = CGGradient(
        colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
        colors: [color(0xF0956A), color(0xB94E2A)] as CFArray,
        locations: [0, 1]
    )!
    ctx.drawLinearGradient(
        gradient,
        start: CGPoint(x: 512, y: body.maxY),
        end: CGPoint(x: 512, y: body.minY),
        options: []
    )
    ctx.restoreGState()

    // Gauge: a 240° arc from bottom-left, over the top, to bottom-right.
    let center = CGPoint(x: 512, y: 470)
    let radius: CGFloat = 255
    let start: CGFloat = 210 * .pi / 180
    let end: CGFloat = -30 * .pi / 180
    let fraction: CGFloat = 0.62
    let level = start - (start - end) * fraction

    ctx.setLineCap(.round)
    ctx.setLineWidth(72)

    // Track.
    ctx.setStrokeColor(color(0xFFFFFF, 0.28))
    ctx.addArc(center: center, radius: radius, startAngle: start, endAngle: end, clockwise: true)
    ctx.strokePath()

    // Filled portion (the share of the budget used).
    ctx.setStrokeColor(color(0xFFFFFF))
    ctx.addArc(center: center, radius: radius, startAngle: start, endAngle: level, clockwise: true)
    ctx.strokePath()

    // Needle pointing at the current level.
    ctx.setLineWidth(38)
    ctx.move(to: center)
    ctx.addLine(to: CGPoint(x: center.x + cos(level) * 175, y: center.y + sin(level) * 175))
    ctx.strokePath()

    // Hub.
    ctx.setFillColor(color(0xFFFFFF))
    ctx.fillEllipse(in: CGRect(x: center.x - 52, y: center.y - 52, width: 104, height: 104))
    ctx.setFillColor(color(0xC8603A))
    ctx.fillEllipse(in: CGRect(x: center.x - 20, y: center.y - 20, width: 40, height: 40))
}

func writePNG(pixels: Int, to url: URL) throws {
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    guard let ctx = CGContext(
        data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: 0,
        space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else { throw NSError(domain: "make-icon", code: 1) }
    ctx.interpolationQuality = .high
    ctx.setShouldAntialias(true)
    let scale = CGFloat(pixels) / 1024
    ctx.scaleBy(x: scale, y: scale)
    drawIcon(in: ctx)

    guard let image = ctx.makeImage(),
          let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)
    else { throw NSError(domain: "make-icon", code: 2) }
    CGImageDestinationAddImage(dest, image, nil)
    guard CGImageDestinationFinalize(dest) else { throw NSError(domain: "make-icon", code: 3) }
}

let args = CommandLine.arguments
guard args.count == 2 else {
    FileHandle.standardError.write("usage: swift make-icon.swift <output.iconset>\n".data(using: .utf8)!)
    exit(1)
}
let outDir = URL(fileURLWithPath: args[1])
try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)

for base in [16, 32, 128, 256, 512] {
    try writePNG(pixels: base, to: outDir.appendingPathComponent("icon_\(base)x\(base).png"))
    try writePNG(pixels: base * 2, to: outDir.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
print("Wrote \(outDir.path)")
