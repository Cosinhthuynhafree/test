import Foundation
import Security

final class SessionKeychain {
    private let service = "com.hoshino.transfer.session.v1"
    private let account = "session"

    func load() -> StoredSession? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return try? JSONDecoder().decode(StoredSession.self, from: data)
    }

    @discardableResult
    func save(_ session: StoredSession) -> Bool {
        guard let data = try? JSONEncoder().encode(session) else { return false }
        var attributes = baseQuery
        attributes[kSecValueData as String] = data
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(attributes as CFDictionary, nil)
        if status == errSecSuccess { return true }
        guard status == errSecDuplicateItem else { return false }
        return SecItemUpdate(baseQuery as CFDictionary, [kSecValueData as String: data] as CFDictionary) == errSecSuccess
    }

    func delete() {
        SecItemDelete(baseQuery as CFDictionary)
    }

    private var baseQuery: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }
}
