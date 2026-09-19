import 'package:clinic_core/clinic_core.dart';
import 'package:dio/dio.dart';

/// Builds the app's Dio engine from the shared [ApiConfig].
///
/// Phase 1A-1 constructs the client only; no endpoint calls exist yet.
/// Later slices add authentication interceptors (session token, MFA
/// refresh) without changing this construction point.
final class ApiClientFactory {
  const ApiClientFactory(this.config);

  final ApiConfig config;

  Dio create() {
    final dio = Dio(
      BaseOptions(
        baseUrl: config.baseUrl,
        connectTimeout: config.connectTimeout,
        receiveTimeout: config.receiveTimeout,
        // The API speaks JSON; failures arrive as ProblemDetails bodies.
        responseType: ResponseType.json,
        headers: {'Accept': 'application/json'},
      ),
    );
    // Reserved: an interceptor that translates DioExceptions into the
    // clinic_core ApiError classification will be registered here.
    return dio;
  }
}
