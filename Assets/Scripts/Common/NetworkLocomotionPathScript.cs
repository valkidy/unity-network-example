using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NetworkExample.UnityDemo.Common
{
    /// <summary>
    /// Managed port of the native capture harness path script
    /// (engine/src/tests/capture/path_script.h), so a Unity locomotion run and a
    /// <c>//engine/src/tests/kernel_tests:locomotion_capture</c> run can be driven
    /// with the same <c>--path</c> text and compared directly.
    ///
    /// The move vector is in the world XZ plane: x is world +X, y is world +Z.
    /// The script only decides where the input points; how fast the body yaws to
    /// face it stays with the kernel (max_yaw_degrees_per_second).
    /// </summary>
    public sealed class NetworkLocomotionPathScript
    {
        public enum StepKind
        {
            SetHeading,
            Forward,
            Turn,
            Wait,
        }

        public struct Step
        {
            public StepKind Kind;
            public float Value;
            public Vector2 Heading;
        }

        private readonly List<Step> steps = new List<Step>();
        private float tickRateHz = 30f;
        private string description = string.Empty;
        private Vector2 heading = new Vector2(1f, 0f);
        private Vector2 initialHeading = new Vector2(1f, 0f);
        private Vector3 stepAnchor;
        private int stepIndex;
        private int waitTicksLeft;
        private bool stepEntered;

        public bool Finished => stepIndex >= steps.Count;
        public string Description => description;
        public IReadOnlyList<Step> Steps => steps;

        /// <summary>
        /// True when the path can never finish, because a step walks forever --
        /// which is what a bare axis alias such as "+X" expands to. A driver that
        /// replays the path on <see cref="Finished"/> never gets its turn.
        /// </summary>
        public bool IsUnbounded
        {
            get
            {
                for (int index = 0; index < steps.Count; ++index)
                {
                    if (steps[index].Kind == StepKind.Forward &&
                        float.IsInfinity(steps[index].Value))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public static bool TryParse(
            string text,
            float tickRateHz,
            out NetworkLocomotionPathScript script,
            out string error)
        {
            script = null;
            error = null;
            if (!(tickRateHz > 0f))
            {
                error = "tick rate must be positive";
                return false;
            }

            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                error = "path is empty";
                return false;
            }

            var parsed = new NetworkLocomotionPathScript { tickRateHz = tickRateHz };

            // A bare axis alias is the common case: walk that way for the whole run.
            if (TryParseAxis(trimmed, out Vector2 axisHeading))
            {
                parsed.steps.Add(new Step
                {
                    Kind = StepKind.SetHeading,
                    Heading = axisHeading,
                });
                parsed.steps.Add(new Step
                {
                    Kind = StepKind.Forward,
                    Value = float.PositiveInfinity,
                });
                parsed.heading = axisHeading;
                parsed.initialHeading = axisHeading;
                parsed.description = trimmed;
                script = parsed;
                return true;
            }

            foreach (string rawStep in SplitSteps(trimmed))
            {
                string stepText = rawStep.Trim();
                if (stepText.Length == 0)
                {
                    continue;
                }
                if (TryParseAxis(stepText, out axisHeading))
                {
                    parsed.steps.Add(new Step
                    {
                        Kind = StepKind.SetHeading,
                        Heading = axisHeading,
                    });
                    continue;
                }

                int numberBegin = 0;
                while (numberBegin < stepText.Length &&
                       !StartsNumber(stepText, numberBegin))
                {
                    ++numberBegin;
                }
                if (numberBegin >= stepText.Length)
                {
                    error = "path step '" + stepText + "' has no number";
                    return false;
                }

                string keyword = stepText.Substring(0, numberBegin).Trim().ToLowerInvariant();
                if (keyword.Length == 0)
                {
                    error =
                        "path step '" + stepText +
                        "' has no keyword (expected forward/turn/wait)";
                    return false;
                }
                if (!TryParseLeadingFloat(stepText.Substring(numberBegin), out float value))
                {
                    error = "path step '" + stepText + "' has an invalid number";
                    return false;
                }

                var step = new Step { Value = value };
                if (Matches(keyword, "forward", "fwd", "move", "前進", "前进"))
                {
                    if (value <= 0f)
                    {
                        error = "forward distance must be positive: " + stepText;
                        return false;
                    }
                    step.Kind = StepKind.Forward;
                }
                else if (Matches(keyword, "turn", "rotate", "旋轉", "旋转", "轉", "转"))
                {
                    step.Kind = StepKind.Turn;
                }
                else if (Matches(keyword, "wait", "hold", "等待", "停"))
                {
                    if (value < 0f)
                    {
                        error = "wait duration must not be negative: " + stepText;
                        return false;
                    }
                    step.Kind = StepKind.Wait;
                }
                else
                {
                    error =
                        "unknown path keyword '" + keyword + "' in '" + stepText + "'";
                    return false;
                }
                parsed.steps.Add(step);
            }

            if (parsed.steps.Count == 0)
            {
                error = "path has no steps";
                return false;
            }

            parsed.description = Describe(parsed.steps);
            script = parsed;
            return true;
        }

        /// <summary>
        /// Rewinds to the first step, so a finite path can be walked over and
        /// over as a patrol circuit.
        ///
        /// The native harness runs a path once and stops, so this is deliberately
        /// a driver-side control rather than a new keyword: the <c>--path</c> text
        /// stays byte-for-byte comparable with a
        /// <c>//engine/src/tests/kernel_tests:locomotion_capture</c> run.
        ///
        /// Forward steps re-anchor on the position they are entered at, so a
        /// replayed circuit is walked relative to wherever the subject ended up,
        /// not to the original spawn.
        /// </summary>
        public void Restart()
        {
            stepIndex = 0;
            stepEntered = false;
            waitTicksLeft = 0;
            heading = initialHeading;
        }

        /// <summary>
        /// Call once per kernel tick with the subject's current world position.
        /// Returns the move input for the next kernel update.
        /// </summary>
        public Vector2 MoveInput(Vector3 worldPosition)
        {
            while (stepIndex < steps.Count)
            {
                if (!stepEntered)
                {
                    EnterStep(worldPosition);
                }

                Step step = steps[stepIndex];
                switch (step.Kind)
                {
                    case StepKind.SetHeading:
                        heading = step.Heading;
                        Advance();
                        break;

                    case StepKind.Turn:
                    {
                        float radians = step.Value * Mathf.Deg2Rad;
                        float cosine = Mathf.Cos(radians);
                        float sine = Mathf.Sin(radians);
                        heading = new Vector2(
                            heading.x * cosine - heading.y * sine,
                            heading.x * sine + heading.y * cosine);
                        float length = heading.magnitude;
                        if (length > 0f)
                        {
                            heading /= length;
                        }
                        Advance();
                        break;
                    }

                    case StepKind.Wait:
                        if (waitTicksLeft <= 0)
                        {
                            Advance();
                            break;
                        }
                        --waitTicksLeft;
                        return Vector2.zero;

                    case StepKind.Forward:
                    {
                        float dx = worldPosition.x - stepAnchor.x;
                        float dz = worldPosition.z - stepAnchor.z;
                        float travelled = Mathf.Sqrt(dx * dx + dz * dz);
                        if (travelled >= step.Value)
                        {
                            Advance();
                            break;
                        }
                        return heading;
                    }
                }
            }

            return Vector2.zero;
        }

        private void Advance()
        {
            ++stepIndex;
            stepEntered = false;
        }

        private void EnterStep(Vector3 worldPosition)
        {
            Step step = steps[stepIndex];
            switch (step.Kind)
            {
                case StepKind.Forward:
                    stepAnchor = worldPosition;
                    break;
                case StepKind.Wait:
                    waitTicksLeft = Mathf.RoundToInt(step.Value * tickRateHz);
                    break;
            }
            stepEntered = true;
        }

        private static bool TryParseAxis(string token, out Vector2 value)
        {
            string compact = string.Empty;
            for (int index = 0; index < token.Length; ++index)
            {
                if (!char.IsWhiteSpace(token[index]))
                {
                    compact += token[index];
                }
            }
            compact = compact.ToLowerInvariant();
            switch (compact)
            {
                case "x":
                case "+x":
                    value = new Vector2(1f, 0f);
                    return true;
                case "-x":
                    value = new Vector2(-1f, 0f);
                    return true;
                case "z":
                case "+z":
                    value = new Vector2(0f, 1f);
                    return true;
                case "-z":
                    value = new Vector2(0f, -1f);
                    return true;
            }

            value = default;
            return false;
        }

        private static bool StartsNumber(string text, int index)
        {
            char value = text[index];
            if (char.IsDigit(value) || value == '.')
            {
                return true;
            }
            if ((value == '+' || value == '-') && index + 1 < text.Length)
            {
                char next = text[index + 1];
                return char.IsDigit(next) || next == '.';
            }
            return false;
        }

        // Mirrors strtof: consume the longest numeric prefix and ignore the unit
        // suffix ("10m", "45deg", "2s").
        private static bool TryParseLeadingFloat(string text, out float value)
        {
            int end = 0;
            if (end < text.Length && (text[end] == '+' || text[end] == '-'))
            {
                ++end;
            }
            while (end < text.Length && char.IsDigit(text[end]))
            {
                ++end;
            }
            if (end < text.Length && text[end] == '.')
            {
                ++end;
                while (end < text.Length && char.IsDigit(text[end]))
                {
                    ++end;
                }
            }

            return float.TryParse(
                       text.Substring(0, end),
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out value) &&
                   !float.IsNaN(value) &&
                   !float.IsInfinity(value);
        }

        private static bool Matches(string keyword, params string[] aliases)
        {
            for (int index = 0; index < aliases.Length; ++index)
            {
                if (string.Equals(keyword, aliases[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string[] SplitSteps(string text)
        {
            return text.Split(';', ',', '\n');
        }

        private static string Describe(List<Step> steps)
        {
            string text = string.Empty;
            for (int index = 0; index < steps.Count; ++index)
            {
                if (text.Length != 0)
                {
                    text += "; ";
                }

                Step step = steps[index];
                switch (step.Kind)
                {
                    case StepKind.SetHeading:
                        text += "heading(" + Format(step.Heading.x) + ", " +
                            Format(step.Heading.y) + ")";
                        break;
                    case StepKind.Forward:
                        text += float.IsInfinity(step.Value)
                            ? "forward (unbounded)"
                            : "forward " + Format(step.Value) + "m";
                        break;
                    case StepKind.Turn:
                        text += "turn " + Format(step.Value) + "deg";
                        break;
                    case StepKind.Wait:
                        text += "wait " + Format(step.Value) + "s";
                        break;
                }
            }
            return text;
        }

        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
