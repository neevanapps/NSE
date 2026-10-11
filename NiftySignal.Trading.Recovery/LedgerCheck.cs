using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Recovery;

/// <summary>Structural and hash self-check of a durable run before anything is replayed against it (contract section A.18.6): a corrupt or incomplete ledger halts instead of being trusted.</summary>
public static class LedgerCheck
{
    public static void Verify(RunSnapshot snapshot)
    {
        var problems = new List<string>();
        foreach (var control in snapshot.Controls.Where(c => !c.HashIsValid))
        {
            problems.Add($"control '{control.Id}': hash mismatch");
        }

        var ids = snapshot.Controls.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != snapshot.Controls.Count)
        {
            problems.Add("duplicate control identity");
        }

        long nextOutput = 0;
        for (var i = 0; i < snapshot.Steps.Count; i++)
        {
            var step = snapshot.Steps[i];
            if (step.StepSeq != i)
            {
                problems.Add($"step sequence gap: position {i} holds step {step.StepSeq}");
                break;
            }

            if (!step.HashIsValid)
            {
                problems.Add($"step {i}: hash mismatch");
            }

            if (step.OutputFrom != nextOutput || step.OutputToExclusive < step.OutputFrom)
            {
                problems.Add($"step {i}: output range [{step.OutputFrom},{step.OutputToExclusive}) is not contiguous (expected start {nextOutput})");
                break;
            }

            nextOutput = step.OutputToExclusive;
            foreach (var id in step.AppliedControls)
            {
                if (!ids.Contains(id))
                {
                    problems.Add($"step {i}: applied control '{id}' is not journaled");
                }
            }
        }

        if (snapshot.Outputs.Count != nextOutput)
        {
            problems.Add($"output count {snapshot.Outputs.Count} does not match the steps' total {nextOutput}");
        }
        else
        {
            for (var i = 0; i < snapshot.Outputs.Count; i++)
            {
                var o = snapshot.Outputs[i];
                if (o.Seq != i || !o.HashIsValid)
                {
                    problems.Add($"output {i}: sequence or hash mismatch");
                    break;
                }

                try
                {
                    if (OutputCodec.Encode(OutputCodec.Decode(o.Payload)) != o.Payload)
                    {
                        problems.Add($"output {i}: payload is not canonical");
                        break;
                    }
                }
                catch (Exception e) when (e is FormatException or NotSupportedException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or System.Reflection.TargetInvocationException)
                {
                    problems.Add($"output {i}: undecodable ({e.Message})");
                    break;
                }
            }
        }

        for (var i = 0; i < snapshot.Inputs.Count; i++)
        {
            var input = snapshot.Inputs[i];
            if (input.Seq != i || !input.HashIsValid || input.StepSeq < 0 || input.StepSeq >= snapshot.Steps.Count || (i > 0 && input.StepSeq < snapshot.Inputs[i - 1].StepSeq))
            {
                problems.Add($"input {i}: sequence, step assignment or hash mismatch");
                break;
            }
        }

        foreach (var c in snapshot.Checkpoints.Where(c => c.StepSeq < 0 || c.StepSeq >= snapshot.Steps.Count))
        {
            problems.Add($"checkpoint at step {c.StepSeq} refers to a step that does not exist");
        }

        if (snapshot.Completed != (snapshot.Final is not null) || (snapshot.Completed && (snapshot.Steps.Count == 0 || snapshot.Steps[^1].Kind != Adapters.UnitKind.Finish)))
        {
            problems.Add("the completion marker, the final record and the last step disagree");
        }
        else if (snapshot.Final is { } final && !final.HashIsValid)
        {
            problems.Add("final record: hash mismatch");
        }

        if (snapshot.Projections.Any(p => !p.HashIsValid))
        {
            problems.Add("a durable projection row's hash does not match its payload");
        }

        var derived = ProjectionBuilder.Build(snapshot.Outputs).Select(p => (p.Table, p.Key, p.ContentHash));
        if (!derived.SequenceEqual(snapshot.Projections.Select(p => (p.Table, p.Key, p.ContentHash))))
        {
            problems.Add("durable projections differ from the projections re-derived from the outputs");
        }

        if (problems.Count > 0)
        {
            throw new RecoveryHaltException(HaltKind.CorruptLedger, "The durable ledger failed its self-check; nothing was replayed.", problems);
        }
    }
}
