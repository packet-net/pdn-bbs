using Bbs.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Bbs.Host;

/// <summary>
/// YamlDotNet converter for a connect script (<c>List&lt;ConnectStep&gt;</c>). It emits a block sequence
/// of step maps (the canonical structured form), and on read tolerates the RETIRED flat form: if any
/// item is a plain scalar (a legacy <c>C GB7RDG</c> / <c>EXPECT=SEND</c> line) the whole script — even if
/// it also contains structured maps — degrades to EMPTY rather than throwing, so a <c>bbs.yaml</c> still
/// carrying the retired flat form still boots (the partner is then inbound-only until the sysop authors a
/// structured script). A structurally malformed step <em>map</em> is NOT degraded: like any other bad
/// value in <c>bbs.yaml</c> it fails Parse and is surfaced at startup. See <c>docs/connect-script-v2.md</c>.
/// </summary>
internal sealed class ConnectScriptYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(List<ConnectStep>);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(rootDeserializer);

        var steps = new List<ConnectStep>();
        bool legacy = false;
        parser.Consume<SequenceStart>();
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            if (parser.Current is Scalar)
            {
                // A legacy flat line — the form is retired; degrade the whole script to blank.
                parser.MoveNext();
                legacy = true;
            }
            else if (rootDeserializer(typeof(ConnectStep)) is ConnectStep step)
            {
                steps.Add(step);
            }
        }

        return legacy ? new List<ConnectStep>() : steps;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(serializer);

        var steps = value as List<ConnectStep> ?? [];
        emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));
        foreach (ConnectStep step in steps)
        {
            serializer(step, typeof(ConnectStep));
        }

        emitter.Emit(new SequenceEnd());
    }
}
