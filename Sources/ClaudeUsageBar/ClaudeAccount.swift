import Foundation
import UsageCore

/// Fetches the signed-in account's plan usage from Claude, using Claude Code's saved login.
///
/// - The login is read from the macOS Keychain item Claude Code writes ("Claude Code-credentials")
///   through `/usr/bin/security`, the same way Claude Code reads it, so macOS doesn't prompt.
/// - The token is only ever sent to api.anthropic.com, and is never logged or stored by this app.
/// - The token is never refreshed here: refreshing rotates it and could sign Claude Code out.
///   When it has expired, using Claude Code renews it.
enum ClaudeAccount {
    enum FetchError: Error {
        case notSignedIn
        case expired
        case rejected
        case badResponse
        case network(String)

        var message: String {
            switch self {
            case .notSignedIn: return "Not signed in to Claude Code with a Claude account."
            case .expired, .rejected: return "Claude Code's login has expired. Use Claude Code to renew it."
            case .badResponse: return "Claude returned usage data this app doesn't understand."
            case .network(let detail): return "Couldn't reach Claude: \(detail)"
            }
        }
    }

    private static let endpoint = URL(string: "https://api.anthropic.com/api/oauth/usage")!

    static func fetchPlanUsage() async throws -> PlanUsage {
        let token = try accessToken()
        var request = URLRequest(url: endpoint, timeoutInterval: 15)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.setValue("ClaudeUsageBar/1.0", forHTTPHeaderField: "User-Agent")
        request.cachePolicy = .reloadIgnoringLocalCacheData

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await URLSession.shared.data(for: request)
        } catch {
            throw FetchError.network(error.localizedDescription)
        }
        guard let http = response as? HTTPURLResponse else { throw FetchError.badResponse }
        switch http.statusCode {
        case 200: break
        case 401, 403: throw FetchError.rejected
        default: throw FetchError.network("HTTP \(http.statusCode)")
        }
        guard let usage = PlanUsage.parse(data) else { throw FetchError.badResponse }
        return usage
    }

    /// Returns Claude Code's current access token, or throws if there isn't a usable one.
    private static func accessToken() throws -> String {
        guard let json = keychainCredentials() ?? fileCredentials(),
              let object = try? JSONSerialization.jsonObject(with: json) as? [String: Any],
              let oauth = object["claudeAiOauth"] as? [String: Any],
              let token = oauth["accessToken"] as? String, !token.isEmpty
        else { throw FetchError.notSignedIn }

        if let expiresAt = oauth["expiresAt"] as? Double,
           Date(timeIntervalSince1970: expiresAt / 1000) <= Date() {
            throw FetchError.expired
        }
        return token
    }

    private static func keychainCredentials() -> Data? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        process.arguments = ["find-generic-password", "-s", "Claude Code-credentials", "-w"]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return nil
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0, !data.isEmpty else { return nil }
        return data
    }

    /// Claude Code falls back to this file where the Keychain isn't used.
    private static func fileCredentials() -> Data? {
        let url = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".claude/.credentials.json")
        return try? Data(contentsOf: url)
    }
}
