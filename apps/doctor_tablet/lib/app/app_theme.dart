import 'package:flutter/material.dart';

/// Clinical, calm visual base for the tablet workspace.
final class AppTheme {
  AppTheme._();

  static const Color seedColor = Color(0xFF00696D);

  static ThemeData light() => ThemeData(
        useMaterial3: true,
        colorScheme: ColorScheme.fromSeed(seedColor: seedColor),
        appBarTheme: const AppBarTheme(centerTitle: false),
      );
}
