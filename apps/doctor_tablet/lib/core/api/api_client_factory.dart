import 'package:clinic_core/clinic_core.dart';
import 'package:dio/dio.dart';

/// Builds the app's Dio engine from the shared [ApiConfig].
///
/// MobileAuth adds authentication only to its protected client. No automatic
/// retries, redirects, certificate bypasses, or credential logging are enabled.
final class ApiClientFactory {
  const ApiClientFactory(this.config);

  final ApiConfig config;

  Dio create() {
    final dio = Dio(
      BaseOptions(
        baseUrl: config.baseUrl,
        connectTimeout: config.connectTimeout,
        receiveTimeout: config.receiveTimeout,
        sendTimeout: config.connectTimeout,
        followRedirects: false,
        // The API speaks JSON; failures arrive as ProblemDetails bodies.
        responseType: ResponseType.json,
        headers: {'Accept': 'application/json'},
      ),
    );
    return dio;
  }
}
