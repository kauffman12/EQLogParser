using System.Collections.Generic;

namespace EQLogParser;

internal sealed record WebhookAggregationOption(int Value, string Text);

internal static class WebhookAggregation
{
  // Values 0–6 are the fork's persisted choices; 1s adds a choice without reinterpreting saved triggers.
  internal static IReadOnlyList<WebhookAggregationOption> Options { get; } =
  [
    new(0, "Aggregation Off"), new(7, "Aggregate: 1s"), new(1, "Aggregate: 5s"),
    new(2, "Aggregate: 10s"), new(3, "Aggregate: 15s"), new(4, "Aggregate: 20s"),
    new(5, "Aggregate: 25s"), new(6, "Aggregate: 30s")
  ];

  internal static int SecondsFor(int value) => value switch
  {
    7 => 1,
    >= 1 and <= 6 => value * 5,
    _ => 0
  };
}