// MultiClaude: menubar manager for side-by-side Claude desktop instances.
//
// The heavy lifting (building/signing bundles, upgrades, MCP + routine copies,
// deferred ops) lives in the `multiclaude` engine shipped in Resources; this app
// is the UI over `multiclaude --json …` plus the Claude window cycler
// (⌘` / ⇧⌘` / ⌃`, active only while a Claude instance is frontmost), which used
// to live in ~/.hammerspoon/init.lua.

import AppKit
import ApplicationServices
import Carbon.HIToolbox
import ServiceManagement
import SwiftUI

// MARK: - Engine bridge

struct Instance: Decodable, Identifiable, Hashable {
    let name: String
    let bundle: String
    let bundle_id: String
    let profile: String
    let color: String
    let glyph: String
    let tint: Bool?
    let version: String?
    let exists: Bool?
    let running: Bool?
    let mode: String?
    let staged: String?
    let icon_png: String?
    var id: String { name }
}

struct PendingOp: Decodable, Identifiable, Hashable {
    let id: String
    let op: String
    let name: String?
    let src: String?
    let dst: String?
    let wait: [String]?
    let label: String?
}

struct ListResult: Decodable {
    let ok: Bool
    let instances: [Instance]?
    let pending: [PendingOp]?
    let status: Status?
    let latest: Latest?
    struct Status: Decodable { let at: Double?; let msg: String? }
    struct Latest: Decodable { let checked: Double?; let version: String?; let installed_min: String?; let size: Int? }
}

struct EngineReply: Decodable {
    let ok: Bool
    let error: String?
    let msg: String?
    let deferred: Bool?
    let version: String?
}

enum Engine {
    static var path: String {
        Bundle.main.path(forResource: "multiclaude", ofType: nil) ?? "\(NSHomeDirectory())/code/chez-claude/multiclaude/multiclaude"
    }

    /// Runs the engine off the main thread; returns (stdout, stderr, exit code).
    static func run(_ args: [String]) async -> (Data, String, Int32) {
        await withCheckedContinuation { cont in
            DispatchQueue.global(qos: .userInitiated).async {
                let p = Process()
                p.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
                p.arguments = [path, "--json"] + args
                let out = Pipe(), err = Pipe()
                p.standardOutput = out
                p.standardError = err
                do { try p.run() } catch {
                    cont.resume(returning: (Data(), "\(error)", -1)); return
                }
                // Drain both pipes concurrently so a chatty build can't block.
                var errData = Data()
                let g = DispatchGroup(); g.enter()
                DispatchQueue.global().async { errData = err.fileHandleForReading.readDataToEndOfFile(); g.leave() }
                let outData = out.fileHandleForReading.readDataToEndOfFile()
                g.wait(); p.waitUntilExit()
                cont.resume(returning: (outData, String(data: errData, encoding: .utf8) ?? "", p.terminationStatus))
            }
        }
    }

    static func reply(_ args: [String]) async -> EngineReply {
        let (o, e, _) = await run(args)
        if let r = try? JSONDecoder().decode(EngineReply.self, from: lastLine(o)) { return r }
        return EngineReply(ok: false, error: e.split(separator: "\n").last.map(String.init) ?? "engine failed", msg: nil, deferred: nil, version: nil)
    }

    static func lastLine(_ d: Data) -> Data {
        let s = String(data: d, encoding: .utf8) ?? ""
        return Data((s.split(separator: "\n").last.map(String.init) ?? "").utf8)
    }
}

// MARK: - Model

@MainActor
final class Model: ObservableObject {
    @Published var instances: [Instance] = []
    @Published var pending: [PendingOp] = []
    @Published var status: String = ""
    @Published var latest: String? = nil
    @Published var installedMin: String? = nil
    @Published var busy: String? = nil
    @Published var lastError: String? = nil
    @Published var axTrusted = AXIsProcessTrusted()
    @Published var cyclerOn = UserDefaults.standard.object(forKey: "cyclerOn") as? Bool ?? true {
        didSet { UserDefaults.standard.set(cyclerOn, forKey: "cyclerOn"); Cycler.shared.refresh() }
    }
    private var timer: Timer?

    init() {
        Task { await refresh() }
        timer = Timer.scheduledTimer(withTimeInterval: 6, repeats: true) { [weak self] _ in
            Task { @MainActor in
                await self?.refresh()
                self?.axTrusted = AXIsProcessTrusted()
            }
        }
        // Daily background update check (just the feed query, no download).
        Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.checkUpdates(silent: true) }
        }
        Task { await checkUpdates(silent: true) }
    }

    func refresh() async {
        let (o, _, _) = await Engine.run(["list"])
        guard let r = try? JSONDecoder().decode(ListResult.self, from: Engine.lastLine(o)) else { return }
        instances = r.instances ?? []
        pending = r.pending ?? []
        status = r.status?.msg ?? ""
        latest = r.latest?.version
        installedMin = r.latest?.installed_min
        Cycler.shared.bundleIDs = Set(instances.map(\.bundle_id) + ["com.anthropic.claudefordesktop"])
        Cycler.shared.autolaunchIDs = instances.map(\.bundle_id)
        Cycler.shared.refresh()
    }

    var updateAvailable: Bool {
        guard let l = latest else { return false }
        return instances.contains { ($0.version ?? "0").compare(l, options: .numeric) == .orderedAscending }
    }

    func act(_ label: String, _ args: [String]) {
        Task {
            busy = label
            let r = await Engine.reply(args)
            busy = nil
            if !r.ok { lastError = r.error; alert("\(label) failed", r.error ?? "unknown error") }
            else if let m = r.msg { notifyBanner(m) }
            await refresh()
        }
    }

    func checkUpdates(silent: Bool) async {
        let r = await Engine.reply(["upgrade", "--check"])
        await refresh()
        if !silent {
            if !r.ok { alert("Update check failed", r.error ?? "") }
            else if !updateAvailable { alert("Up to date", "Every instance is on \(latest ?? "the latest") or newer.") }
        }
    }

    func upgradeAll() {
        guard let v = latest else { return }
        let running = instances.filter { $0.running == true }.map { "Claude \($0.name)" }
        var info = "Downloads Claude \(v) from Anthropic's update feed and rebuilds every instance (theme, icon, profile)."
        if !running.isEmpty { info += "\n\nRunning now: \(running.joined(separator: ", ")). Each one switches over the moment you quit it, then relaunches." }
        guard confirm("Upgrade all instances to \(v)?", info, ok: "Upgrade") else { return }
        act("Upgrading to \(v)", ["upgrade"])
    }
}

// MARK: - Window cycler (port of the Hammerspoon Cmd+` cycler)

@_silgen_name("_AXUIElementGetWindow")
func _AXUIElementGetWindow(_ el: AXUIElement, _ wid: UnsafeMutablePointer<CGWindowID>) -> AXError

final class Cycler {
    static let shared = Cycler()
    var bundleIDs: Set<String> = ["com.anthropic.claudefordesktop.personal", "com.anthropic.claudefordesktop.work", "com.anthropic.claudefordesktop"]
    /// Instances that get launched when absent from the cycle (original behavior
    /// for Personal/Work, now every registered instance).
    var autolaunchIDs: [String] = []
    var enabled: () -> Bool = { true }
    private var refs: [EventHotKeyRef] = []
    private var handlerInstalled = false
    private(set) var count = 0

    // (keycode, modifiers, direction)
    private let keys: [(UInt32, UInt32, Int)] = [
        (UInt32(kVK_ANSI_Grave), UInt32(cmdKey), 1),
        (UInt32(kVK_ANSI_Grave), UInt32(cmdKey | shiftKey), -1),
        (UInt32(kVK_ANSI_Grave), UInt32(controlKey), 1),
    ]

    func start() {
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] _ in
            self?.refresh()
        }
        refresh()
    }

    /// Hotkeys exist only while a Claude instance is frontmost, so ⌘` keeps its
    /// normal meaning in every other app.
    func refresh() {
        let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? ""
        let want = enabled() && AXIsProcessTrusted() && bundleIDs.contains(front)
        if want && refs.isEmpty { register() } else if !want && !refs.isEmpty { unregister() }
    }

    private func register() {
        if !handlerInstalled {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, ev, _ in
                var hk = EventHotKeyID()
                GetEventParameter(ev, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
                Cycler.shared.cycle(Int(hk.id) == 2 ? -1 : 1)
                return noErr
            }, 1, &spec, nil, nil)
            handlerInstalled = true
        }
        for (i, k) in keys.enumerated() {
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: OSType(0x4D434C43), id: UInt32(i + 1)) // 'MCLC'
            if RegisterEventHotKey(k.0, k.1, id, GetApplicationEventTarget(), 0, &ref) == noErr, let ref { refs.append(ref) }
        }
    }

    private func unregister() {
        refs.forEach { UnregisterEventHotKey($0) }
        refs.removeAll()
    }

    struct Win { let wid: CGWindowID; let el: AXUIElement; let app: NSRunningApplication }

    func windows() -> [Win] {
        var out: [Win] = []
        for app in NSWorkspace.shared.runningApplications where bundleIDs.contains(app.bundleIdentifier ?? "") {
            let axApp = AXUIElementCreateApplication(app.processIdentifier)
            var v: CFTypeRef?
            guard AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &v) == .success,
                  let wins = v as? [AXUIElement] else { continue }
            for w in wins {
                var sub: CFTypeRef?
                AXUIElementCopyAttributeValue(w, kAXSubroleAttribute as CFString, &sub)
                guard (sub as? String) == (kAXStandardWindowSubrole as String) else { continue }
                var wid: CGWindowID = 0
                if _AXUIElementGetWindow(w, &wid) == .success { out.append(Win(wid: wid, el: w, app: app)) }
            }
        }
        return out.sorted { $0.wid < $1.wid }
    }

    func cycle(_ dir: Int) {
        count += 1
        let wins = windows()
        let present = Set(wins.compactMap { $0.app.bundleIdentifier })
        // An instance with no window in the pool gets brought up instead of cycling.
        for id in autolaunchIDs where !present.contains(id) {
            if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) {
                NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
                return
            }
        }
        guard !wins.isEmpty else { return }
        var focused: CFTypeRef?
        let sys = AXUIElementCreateSystemWide()
        var fid: CGWindowID = 0
        if AXUIElementCopyAttributeValue(sys, kAXFocusedUIElementAttribute as CFString, &focused) == .success {
            var winRef: CFTypeRef?
            if AXUIElementCopyAttributeValue(focused as! AXUIElement, kAXWindowAttribute as CFString, &winRef) == .success {
                _ = _AXUIElementGetWindow(winRef as! AXUIElement, &fid)
            }
        }
        if fid == 0, let front = NSWorkspace.shared.frontmostApplication {
            let axApp = AXUIElementCreateApplication(front.processIdentifier)
            var w: CFTypeRef?
            if AXUIElementCopyAttributeValue(axApp, kAXFocusedWindowAttribute as CFString, &w) == .success {
                _ = _AXUIElementGetWindow(w as! AXUIElement, &fid)
            }
        }
        let idx = wins.firstIndex { $0.wid == fid } ?? -1
        let next = wins[((idx + dir) % wins.count + wins.count) % wins.count]
        AXUIElementPerformAction(next.el, kAXRaiseAction as CFString)
        AXUIElementSetAttributeValue(next.el, kAXMainAttribute as CFString, kCFBooleanTrue)
        next.app.activate()
    }
}

// MARK: - Helpers

@MainActor func alert(_ title: String, _ info: String) {
    NSApp.activate(ignoringOtherApps: true)
    let a = NSAlert(); a.messageText = title; a.informativeText = info; a.runModal()
}

@MainActor func confirm(_ title: String, _ info: String, ok: String) -> Bool {
    NSApp.activate(ignoringOtherApps: true)
    let a = NSAlert(); a.messageText = title; a.informativeText = info
    a.addButton(withTitle: ok); a.addButton(withTitle: "Cancel")
    return a.runModal() == .alertFirstButtonReturn
}

func notifyBanner(_ msg: String) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
    p.arguments = ["-e", "display notification \(String(reflecting: msg)) with title \"MultiClaude\""]
    try? p.run()
}

extension Color {
    init(hex: String) {
        let v = UInt64(hex.trimmingCharacters(in: CharacterSet(charactersIn: "#")), radix: 16) ?? 0
        self.init(red: Double((v >> 16) & 0xFF) / 255, green: Double((v >> 8) & 0xFF) / 255, blue: Double(v & 0xFF) / 255)
    }
    var hex: String {
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .black
        return String(format: "#%02X%02X%02X", Int(round(c.redComponent * 255)), Int(round(c.greenComponent * 255)), Int(round(c.blueComponent * 255)))
    }
}

func iconImage(_ inst: Instance) -> NSImage {
    let img = (inst.icon_png.flatMap { NSImage(contentsOfFile: $0) }) ?? NSWorkspace.shared.icon(forFile: inst.bundle)
    let out = NSImage(size: NSSize(width: 16, height: 16))
    out.lockFocus()
    img.draw(in: NSRect(x: 0, y: 0, width: 16, height: 16))
    out.unlockFocus()
    return out
}

// MARK: - Menu

struct InstanceMenu: View {
    @ObservedObject var model: Model
    let inst: Instance
    @Environment(\.openWindow) private var openWindow

    var others: [Instance] { model.instances.filter { $0.name != inst.name } }

    var title: String {
        var t = "Claude \(inst.name)"
        if let v = inst.version { t += "  ·  \(v)" }
        if let m = inst.mode, m.hasPrefix("3p") { t += "  ·  \(m)" }
        if inst.running == true { t += "  ●" }
        return t
    }

    var body: some View {
        Menu {
            Button(inst.running == true ? "Show" : "Open") { model.act("Open", ["launch", inst.name]) }
            if inst.running == true { Button("Quit") { model.act("Quit", ["quit", inst.name]) } }
            Divider()
            Button("Change Color…") { openWindow(id: "edit", value: inst.name); NSApp.activate(ignoringOtherApps: true) }
            if !others.isEmpty {
                Menu("Copy MCP Servers To") {
                    ForEach(others) { o in Button("Claude \(o.name)") { model.act("Copy MCP", ["copy-mcp", inst.name, o.name]) } }
                }
                Menu("Copy Routines To") {
                    ForEach(others) { o in Button("Claude \(o.name)") { model.act("Copy routines", ["copy-routines", inst.name, o.name]) } }
                }
                Menu("Move Routines To") {
                    ForEach(others) { o in Button("Claude \(o.name)") {
                        if confirm("Move routines to Claude \(o.name)?", "They're copied there and disabled (not deleted) in Claude \(inst.name), so nothing fires twice.", ok: "Move") {
                            model.act("Move routines", ["copy-routines", inst.name, o.name, "--move"])
                        }
                    } }
                }
                if let m = inst.mode, m.hasPrefix("3p") {
                    Menu("Hand \(m.dropFirst(3)) Setup To") {
                        ForEach(others) { o in Button("Claude \(o.name)") {
                            if confirm("Move the 3p setup to Claude \(o.name)?", "Claude \(inst.name) goes back to its claude.ai account; Claude \(o.name) takes over the gateway config and its Code sessions. Runs when both are quit, then relaunches them.", ok: "Move") {
                                model.act("Move 3p", ["move-3p", inst.name, o.name, "--launch"])
                            }
                        } }
                    }
                }
            }
            Divider()
            Button("Show Profile in Finder") {
                NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: "\(NSHomeDirectory())/Library/Application Support/\(inst.profile)")
            }
            Button("Remove Instance…") {
                if confirm("Remove Claude \(inst.name)?", "The app goes to the Trash. Its profile (chats, settings, sessions) stays in Application Support/\(inst.profile).", ok: "Remove") {
                    model.act("Remove", ["remove", inst.name])
                }
            }.disabled(inst.running == true)
        } label: {
            Image(nsImage: iconImage(inst))
            Text(title)
        }
    }
}

struct MenuContent: View {
    @ObservedObject var model: Model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        ForEach(model.instances) { InstanceMenu(model: model, inst: $0) }
        Divider()
        Button("New Instance…") { openWindow(id: "new"); NSApp.activate(ignoringOtherApps: true) }
        if model.updateAvailable, let v = model.latest {
            Button("Upgrade All to \(v)…") { model.upgradeAll() }
        } else {
            Button("Check for Updates") { Task { await model.checkUpdates(silent: false) } }
        }
        if !model.pending.isEmpty {
            Divider()
            Text("Runs when these apps quit")
            ForEach(model.pending) { p in
                Menu("\(p.label ?? "\(p.op) \(p.name ?? "")")  ·  after \((p.wait ?? []).map { "Claude \($0)" }.joined(separator: " + ")) quit") {
                    ForEach(p.wait ?? [], id: \.self) { w in
                        Button("Quit Claude \(w) Now") { model.act("Quit", ["finish", w]) }
                    }
                    Button("Cancel") { model.act("Cancel", ["pending", "--cancel", p.id]) }
                }
            }
        }
        if let b = model.busy { Divider(); Text("⏳ \(b)…") }
        else if !model.status.isEmpty { Divider(); Text(model.status).lineLimit(2) }
        Divider()
        Toggle("Window Cycler (⌘`)", isOn: $model.cyclerOn)
        if model.cyclerOn && !model.axTrusted {
            Button("Grant Accessibility for the Cycler…") {
                let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
                _ = AXIsProcessTrustedWithOptions(opts)
                NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
            }
        }
        Toggle("Launch at Login", isOn: Binding(
            get: { SMAppService.mainApp.status == .enabled },
            set: { on in try? on ? SMAppService.mainApp.register() : SMAppService.mainApp.unregister() }))
        Button("Quit MultiClaude") { NSApp.terminate(nil) }.keyboardShortcut("q")
    }
}

// MARK: - New / edit windows

let swatches = ["#000000", "#15433D", "#3B2A6B", "#1F3A5F", "#6B1F2A", "#7A4A12", "#2E2E2E", "#0B5563", "#5B1A5E", "#3C5A14"]

struct ColorRow: View {
    let title: String
    @Binding var color: Color
    var body: some View {
        HStack {
            Text(title).frame(width: 80, alignment: .leading)
            ColorPicker("", selection: $color, supportsOpacity: false).labelsHidden()
            ForEach(swatches, id: \.self) { h in
                Circle().fill(Color(hex: h)).frame(width: 18, height: 18)
                    .overlay(Circle().stroke(Color.secondary.opacity(0.6), lineWidth: color.hex == h ? 2 : 0.5))
                    .onTapGesture { color = Color(hex: h) }
            }
        }
    }
}

struct IconPreview: View {
    let bg: Color
    let glyph: Color
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 14).fill(bg).frame(width: 64, height: 64)
            Image(systemName: "asterisk").font(.system(size: 36, weight: .bold)).foregroundStyle(glyph)
        }
    }
}

struct NewInstanceView: View {
    @ObservedObject var model: Model
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var bg = Color(hex: "#1F3A5F")
    @State private var glyph = Color(hex: "#FEFCFB")
    @State private var tint = true
    @State private var mcpFrom = ""
    @State private var launch = true
    @State private var working = false
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 14) {
                IconPreview(bg: bg, glyph: glyph)
                VStack(alignment: .leading) {
                    Text("Claude \(name.isEmpty ? "…" : name)").font(.headline)
                    Text("Own app, own login, own profile. Runs alongside the others.").font(.caption).foregroundStyle(.secondary)
                }
            }
            TextField("Name (e.g. Bedrock, Client X)", text: $name).textFieldStyle(.roundedBorder)
            ColorRow(title: "Icon", color: $bg)
            ColorRow(title: "Starburst", color: $glyph)
            Toggle("Tint the app UI with the icon color", isOn: $tint)
            Picker("Copy MCP servers from", selection: $mcpFrom) {
                Text("None").tag("")
                ForEach(model.instances) { Text("Claude \($0.name)").tag($0.name) }
            }
            Toggle("Open it when done", isOn: $launch)
            if let error { Text(error).foregroundStyle(.red).font(.caption) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button(working ? "Building…" : "Create") { create() }
                    .keyboardShortcut(.defaultAction).disabled(name.trimmingCharacters(in: .whitespaces).isEmpty || working)
            }
        }
        .padding(20).frame(width: 520)
    }

    func create() {
        working = true; error = nil
        var args = ["create", name, "--color", bg.hex, "--glyph", glyph.hex]
        if !tint { args.append("--no-tint") }
        if !mcpFrom.isEmpty { args += ["--copy-mcp-from", mcpFrom] }
        if launch { args.append("--launch") }
        Task {
            let r = await Engine.reply(args)
            working = false
            if r.ok { await model.refresh(); dismiss() } else { error = r.error }
        }
    }
}

struct EditInstanceView: View {
    @ObservedObject var model: Model
    let name: String
    @Environment(\.dismiss) private var dismiss
    @State private var bg = Color.black
    @State private var glyph = Color.white
    @State private var tint = true
    @State private var loaded = false
    @State private var working = false
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 14) {
                IconPreview(bg: bg, glyph: glyph)
                Text("Claude \(name)").font(.headline)
            }
            ColorRow(title: "Icon", color: $bg)
            ColorRow(title: "Starburst", color: $glyph)
            Toggle("Tint the app UI with the icon color", isOn: $tint)
            Text("The UI tint applies right away (⌘R in the app). The icon needs a re-sign, so if the app is open it updates the next time you quit it.")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if let error { Text(error).foregroundStyle(.red).font(.caption) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button(working ? "Applying…" : "Apply") { apply() }.keyboardShortcut(.defaultAction).disabled(working)
            }
        }
        .padding(20).frame(width: 520)
        .onAppear {
            guard !loaded, let i = model.instances.first(where: { $0.name == name }) else { return }
            bg = Color(hex: i.color); glyph = Color(hex: i.glyph); tint = i.tint ?? true; loaded = true
        }
    }

    func apply() {
        working = true; error = nil
        Task {
            let r = await Engine.reply(["recolor", name, "--color", bg.hex, "--glyph", glyph.hex, "--tint", tint ? "on" : "off"])
            working = false
            if r.ok { if let m = r.msg { notifyBanner(m) }; await model.refresh(); dismiss() } else { error = r.error }
        }
    }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ n: Notification) {
        Cycler.shared.enabled = { UserDefaults.standard.object(forKey: "cyclerOn") as? Bool ?? true }
        Cycler.shared.start()
        // Ask once for Accessibility (needed to focus other apps' windows).
        if !AXIsProcessTrusted() && !UserDefaults.standard.bool(forKey: "askedAX") {
            UserDefaults.standard.set(true, forKey: "askedAX")
            let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(opts)
        }
        // Pick up the grant the moment it lands, without a restart.
        Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { _ in Cycler.shared.refresh() }
    }
}

// Entry point: `MultiClaude --render-icon <stock.icns> <#bg> <#glyph> <out.png>`
// is the engine's ImageMagick-free icon recolor; anything else starts the app.
@main
enum Entry {
    static func main() {
        let a = CommandLine.arguments
        if a.count == 6, a[1] == "--render-icon" {
            exit(IconRender.render(src: a[2], bg: a[3], glyph: a[4], out: a[5]) ? 0 : 1)
        }
        MultiClaudeApp.main()
    }
}

enum IconRender {
    static func rgb(_ hex: String) -> (Double, Double, Double) {
        let v = UInt64(hex.trimmingCharacters(in: CharacterSet(charactersIn: "#")), radix: 16) ?? 0
        return (Double((v >> 16) & 0xFF) / 255, Double((v >> 8) & 0xFF) / 255, Double(v & 0xFF) / 255)
    }

    /// Recolor the stock Claude icon: flood the tile with `bg`, keep the
    /// starburst (separated by min(R,G,B), levelled 38%..96%) in `glyph`, keep
    /// the original alpha. Same recipe as the ImageMagick path in the engine.
    static func render(src: String, bg: String, glyph: String, out: String) -> Bool {
        guard let img = NSImage(contentsOfFile: src) else { return false }
        let n = 1024
        var rect = CGRect(x: 0, y: 0, width: n, height: n)
        guard let cg = img.cgImage(forProposedRect: &rect, context: nil, hints: nil),
              let ctx = CGContext(data: nil, width: n, height: n, bitsPerComponent: 8, bytesPerRow: n * 4,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let buf = ctx.data?.bindMemory(to: UInt8.self, capacity: n * n * 4) else { return false }
        ctx.interpolationQuality = .high
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: n, height: n))
        let (br, bgc, bb) = rgb(bg), (gr, gg, gb) = rgb(glyph)
        for i in stride(from: 0, to: n * n * 4, by: 4) {
            let a = Double(buf[i + 3]) / 255
            if a == 0 { continue }
            let m0 = min(Double(buf[i]), Double(buf[i + 1]), Double(buf[i + 2])) / 255 / a
            let m = max(0, min(1, (m0 - 0.38) / (0.96 - 0.38)))
            let r = br + (gr - br) * m, g = bgc + (gg - bgc) * m, b = bb + (gb - bb) * m
            buf[i] = UInt8(max(0, min(255, (r * a * 255).rounded())))
            buf[i + 1] = UInt8(max(0, min(255, (g * a * 255).rounded())))
            buf[i + 2] = UInt8(max(0, min(255, (b * a * 255).rounded())))
        }
        guard let outImg = ctx.makeImage(),
              let dest = CGImageDestinationCreateWithURL(URL(fileURLWithPath: out) as CFURL, "public.png" as CFString, 1, nil)
        else { return false }
        CGImageDestinationAddImage(dest, outImg, nil)
        return CGImageDestinationFinalize(dest)
    }
}

struct MultiClaudeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var model = Model()

    var body: some Scene {
        MenuBarExtra {
            MenuContent(model: model)
        } label: {
            Image(systemName: model.pending.isEmpty ? "square.stack.3d.up.fill" : "square.stack.3d.up.badge.automatic")
        }
        .menuBarExtraStyle(.menu)

        Window("New Claude Instance", id: "new") { NewInstanceView(model: model) }
            .windowResizability(.contentSize)
        WindowGroup("Change Color", id: "edit", for: String.self) { $name in
            if let name { EditInstanceView(model: model, name: name) }
        }
        .windowResizability(.contentSize)
    }
}
