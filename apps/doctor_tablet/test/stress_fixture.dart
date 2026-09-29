import 'package:doctor_tablet/features/notebook/ink/ink_document.dart';

/// Synthetic data only; never import this fixture into production code.
InkDocument stressInk({
  String patient = 'synthetic-patient',
  String page = 'synthetic-page',
  int strokes = 1000,
  int points = 50,
}) => InkDocument(
  patientId: patient,
  pageId: page,
  strokes: [
    for (var s = 0; s < strokes; s++)
      InkStroke(
        id: s,
        color: 0xff000000,
        width: .7,
        points: [
          for (var p = 0; p < points; p++)
            InkPoint(
              x: (p % 200).toDouble(),
              y: (s % 290).toDouble(),
              timeMicros: p * 1000,
              pressure: (p % 10) / 10,
            ),
        ],
      ),
  ],
);
