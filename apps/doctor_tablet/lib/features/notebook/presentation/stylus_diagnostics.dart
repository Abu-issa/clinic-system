import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';

/// No event objects, coordinates, identifiers, clinical models, disk or logging.
final class StylusMetrics {
  String kind = 'none', contact = 'none';
  double pressure = 0, pressureMin = 0, pressureMax = 0;
  int events = 0, suppressedTouches = 0, frames = 0;
  int buildMicros = 0, rasterMicros = 0, maxRasterMicros = 0;
  final _pens = <int>{};
  void event(PointerEvent event) {
    events++;
    kind = event.kind.name;
    pressure = event.pressure;
    pressureMin = event.pressureMin;
    pressureMax = event.pressureMax;
    final pen =
        event.kind == PointerDeviceKind.stylus ||
        event.kind == PointerDeviceKind.invertedStylus;
    if (pen && event is PointerDownEvent) {
      _pens.add(event.pointer);
      contact = 'down';
    }
    if (event.kind == PointerDeviceKind.touch &&
        event is PointerDownEvent &&
        _pens.isNotEmpty) {
      suppressedTouches++;
    }
    if (pen && (event is PointerUpEvent || event is PointerCancelEvent)) {
      _pens.remove(event.pointer);
      contact = event is PointerCancelEvent ? 'cancel' : 'up';
    }
  }

  void timings(List<FrameTiming> timings) {
    for (final timing in timings) {
      frames++;
      buildMicros += timing.buildDuration.inMicroseconds;
      final raster = timing.rasterDuration.inMicroseconds;
      rasterMicros += raster;
      if (raster > maxRasterMicros) maxRasterMicros = raster;
    }
  }
}

/// Compiled out of release/profile routes. Even direct construction renders
/// nothing outside debug mode. Metrics exist only while this screen is open.
class StylusDiagnostics extends StatefulWidget {
  const StylusDiagnostics({super.key});
  @override
  State<StylusDiagnostics> createState() => _StylusDiagnosticsState();
}

class _StylusDiagnosticsState extends State<StylusDiagnostics> {
  final metrics = StylusMetrics();
  final clock = Stopwatch();
  Timer? timer;
  @override
  void initState() {
    super.initState();
    if (!kDebugMode) return;
    clock.start();
    SchedulerBinding.instance.addTimingsCallback(metrics.timings);
    timer = Timer.periodic(const Duration(milliseconds: 250), (_) {
      if (mounted) setState(() {});
    });
  }

  @override
  void dispose() {
    timer?.cancel();
    clock.stop();
    if (kDebugMode) {
      SchedulerBinding.instance.removeTimingsCallback(metrics.timings);
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (!kDebugMode) return const SizedBox.shrink();
    final m = metrics, seconds = clock.elapsedMicroseconds / 1000000;
    return Scaffold(
      appBar: AppBar(title: const Text('DEV · Stylus diagnostics')),
      body: Column(
        children: [
          const Text(
            'Synthetic test surface — no ink or coordinates retained.\nDebug timings are not release-performance evidence.',
          ),
          Text(
            'kind: ${m.kind} · pen: ${m.contact}\npressure: ${m.pressure} / min ${m.pressureMin} / max ${m.pressureMax}\n'
            'events/s: ${(seconds == 0 ? 0 : m.events / seconds).toStringAsFixed(1)} · touch-downs during pen: ${m.suppressedTouches}\n'
            'frames: ${m.frames} · mean build µs: ${m.frames == 0 ? 0 : m.buildMicros ~/ m.frames}\n'
            'mean raster µs: ${m.frames == 0 ? 0 : m.rasterMicros ~/ m.frames} · max raster µs: ${m.maxRasterMicros}',
            textDirection: TextDirection.ltr,
          ),
          Expanded(
            child: Listener(
              key: const Key('diagnostic-surface'),
              behavior: HitTestBehavior.opaque,
              onPointerDown: m.event,
              onPointerMove: m.event,
              onPointerUp: m.event,
              onPointerCancel: m.event,
              child: const ColoredBox(
                color: Color(0xffeeeeee),
                child: Center(child: Text('Pen / touch test area')),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
