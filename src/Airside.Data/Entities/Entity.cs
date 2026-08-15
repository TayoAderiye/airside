namespace Airside.Data.Entities;

/// <summary>
/// Base for every Airside entity.
/// </summary>
/// <remarks>
/// <para>
/// Keys are UUIDv7: sequential, so index locality survives; globally unique, so
/// ids survive a future multi-host merge; and safe to expose, unlike a sequential
/// integer. Generated in application code so an entity has identity before it is
/// saved and a job can reference it immediately.
/// </para>
/// <para>
/// <see cref="RowVersion"/> is an application-managed <see cref="Guid"/> rather
/// than a Postgres <c>xmin</c>, because <c>xmin</c> does not exist on SQLite and
/// Airside supports both.
/// </para>
/// </remarks>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid RowVersion { get; set; } = Guid.CreateVersion7();
}

/// <summary>An entity that is retired rather than removed, so audit references never dangle.</summary>
public interface ISoftDeletable
{
    DateTime? DeletedAt { get; set; }
}
