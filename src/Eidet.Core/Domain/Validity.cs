namespace Eidet.Core.Domain;

public class Validity
{
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;
    public DateTime? ValidUntil { get; set; }

    public bool IsValidAt(DateTime t) =>
        t >= ValidFrom && (ValidUntil == null || t <= ValidUntil.Value);

    public bool IsCurrentlyValid => IsValidAt(DateTime.UtcNow);
}
