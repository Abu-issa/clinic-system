import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

/// Arabic is the clinic's default language; English is the only other
/// supported locale in this slice. Directionality (RTL/LTR) follows the
/// locale automatically.
const Locale defaultLocale = Locale('ar');
const List<Locale> supportedLocales = [Locale('ar'), Locale('en')];

/// Holds the active UI locale.
final class LocaleCubit extends Cubit<Locale> {
  LocaleCubit() : super(defaultLocale);

  static const List<Locale> supported = supportedLocales;

  /// Seeds the locale from the app's initial-locale parameter (Arabic by
  /// default), keeping the cubit the single source of truth for the UI.
  void seed(Locale locale) => emit(locale);

  void toggle() {
    emit(state.languageCode == 'ar' ? const Locale('en') : defaultLocale);
  }
}
