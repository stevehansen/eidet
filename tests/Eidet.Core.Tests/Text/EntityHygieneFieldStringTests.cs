using Eidet.Core.Text;

namespace Eidet.Core.Tests.Text;

public class EntityHygieneFieldStringTests
{
    /// <summary>
    /// The exact 122-char run-on found on memories/P--HC/insight/d4218a58adc8 — a URL fragment the
    /// extractor ran into the following sentence. Both of that memory's entities are noise by shape.
    /// </summary>
    [Fact]
    public void Drops_the_run_on_entity_from_the_field_corpus()
    {
        var a = "/detail/deletion requires the registration secret. Production verification on 2026-07-10 confirmed anonymous GETs to tasks";
        var b = "/task/progress detail routes and both SSE routes now require a valid device credential";

        Assert.Equal(122, a.Length);
        Assert.True(EntityHygiene.IsNoise(a), "122 chars is over the length ceiling");
        Assert.True(EntityHygiene.IsNoise(b), "14 words is over the word ceiling");
        Assert.Empty(EntityHygiene.Clean([a, b]));
    }
}
