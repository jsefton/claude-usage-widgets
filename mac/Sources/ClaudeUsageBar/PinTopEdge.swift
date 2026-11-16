import AppKit
import SwiftUI

/// Keeps the MenuBarExtra panel's top edge fixed under the menu bar when its height changes.
///
/// AppKit windows are positioned by their bottom-left corner, so when SwiftUI resizes the panel
/// (e.g. expanding or collapsing Settings) the top edge moves and the panel drifts away from
/// the menu bar. This watches the hosting window and moves it back so it grows and shrinks
/// from the bottom instead.
struct PinTopEdge: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { PinView() }
    func updateNSView(_ nsView: NSView, context: Context) {}

    final class PinView: NSView {
        private var observers: [NSObjectProtocol] = []
        /// The top edge (screen coordinates) the panel should stay at.
        private var pinnedTop: CGFloat?
        /// True while we are moving the window ourselves, so our own move isn't taken as the new position.
        private var isAdjusting = false

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observers.forEach { NotificationCenter.default.removeObserver($0) }
            observers = []
            guard let window else { return }

            pinnedTop = window.frame.maxY
            let center = NotificationCenter.default
            observers.append(center.addObserver(
                forName: NSWindow.didMoveNotification, object: window, queue: .main
            ) { [weak self] _ in
                // The system placed the panel (e.g. on opening it under the menu bar item): remember where.
                guard let self, !self.isAdjusting, let window = self.window else { return }
                self.pinnedTop = window.frame.maxY
            })
            observers.append(center.addObserver(
                forName: NSWindow.didResizeNotification, object: window, queue: .main
            ) { [weak self] _ in
                self?.restoreTop()
            })
        }

        private func restoreTop() {
            guard let window, let top = pinnedTop else { return }
            let frame = window.frame
            guard abs(frame.maxY - top) > 0.5 else { return }
            isAdjusting = true
            window.setFrameOrigin(NSPoint(x: frame.minX, y: top - frame.height))
            isAdjusting = false
        }

        deinit {
            observers.forEach { NotificationCenter.default.removeObserver($0) }
        }
    }
}
