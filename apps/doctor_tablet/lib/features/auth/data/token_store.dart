import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// One encrypted value, never separate writes for access and refresh tokens.
abstract interface class TokenStore {
  Future<String?> read();
  Future<void> write(String bundle);
  Future<void> clear();
}

final class SecureTokenStore implements TokenStore {
  const SecureTokenStore({
    this.storage = const FlutterSecureStorage(
      iOptions: IOSOptions(
        accessibility: KeychainAccessibility.unlocked_this_device,
      ),
    ),
  });
  final FlutterSecureStorage storage;
  static const key = 'clinic.mobile.credentials.v1';

  @override
  Future<String?> read() => storage.read(key: key);
  @override
  Future<void> write(String bundle) => storage.write(key: key, value: bundle);
  @override
  Future<void> clear() => storage.delete(key: key);
}

final class TokenPair {
  TokenPair.fromJson(Map<String, dynamic> json)
    : access = json['accessToken'] as String,
      refresh = json['refreshToken'] as String,
      accessExpiry = DateTime.parse(json['expiresAtUtc'] as String),
      refreshExpiry = DateTime.parse(json['refreshExpiresAtUtc'] as String) {
    final format = RegExp(r'^[A-Za-z0-9_-]{43}$');
    if (!format.hasMatch(access) || !format.hasMatch(refresh)) {
      throw const FormatException('Invalid credentials response.');
    }
  }
  final String access;
  final String refresh;
  final DateTime accessExpiry;
  final DateTime refreshExpiry;

  String encode(String origin) => jsonEncode({
    'origin': origin,
    'accessToken': access,
    'refreshToken': refresh,
    'expiresAtUtc': accessExpiry.toUtc().toIso8601String(),
    'refreshExpiresAtUtc': refreshExpiry.toUtc().toIso8601String(),
  });

  @override
  String toString() => 'TokenPair(redacted)';
}
