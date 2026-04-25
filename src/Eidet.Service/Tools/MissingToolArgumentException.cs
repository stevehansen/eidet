namespace Eidet.Service.Tools;

/// <summary>
/// Thrown by handler arg-binding helpers when a required argument is missing or the wrong type.
/// The dispatcher maps this to <see cref="ToolStatus.BadRequest"/>.
/// </summary>
public sealed class MissingToolArgumentException : Exception
{
    public MissingToolArgumentException(string field)
        : base($"missing required argument '{field}'")
    {
        Field = field;
    }

    public string Field { get; }
}
