using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;

namespace Weasel.Postgresql.Tables.Partitioning;

public class RangePartitioning: IPartitionStrategy
{
    private readonly List<RangePartition> _ranges = new();

    public IReadOnlyList<RangePartition> Ranges => _ranges;

    /// <summary>
    /// The database columns to use as part of the hashing strategy
    /// </summary>
    public string[] Columns { get; init; }

    public bool HasExistingDefault { get; private set; }

    void IPartitionStrategy.WritePartitionBy(TextWriter writer)
    {
        writer.WriteLine($") PARTITION BY RANGE ({Columns.Join(", ")});");
    }

    PartitionDelta IPartitionStrategy.CreateDelta(Table parent, IPartitionStrategy actual, out IPartition[] missing)
    {
        missing = default;
        if (actual is RangePartitioning other)
        {
            if (!Columns.SequenceEqual(other.Columns))
            {
                return PartitionDelta.Rebuild;
            }

            if (parent.IgnorePartitionsInMigration) return PartitionDelta.None;

            var match = _ranges.OrderBy(x => x.Suffix).ToArray()
                .SequenceEqual(other._ranges.OrderBy(x => x.Suffix).ToArray());

            if (match) return PartitionDelta.None;

            // We've already done a SequenceEqual, so we know the counts aren't the same
            // and if there are more actual partitions than expected, we need to do a rebalance
            if (other._ranges.Count > _ranges.Count) return PartitionDelta.Rebuild;

            // If any partitions are in the actual that are no longer expected, that's an automatic rebuild
            if (other._ranges.Any(x => !_ranges.Contains(x))) return PartitionDelta.Rebuild;

            missing = _ranges.Where(x => !other._ranges.Contains(x)).OfType<IPartition>().ToArray();
            return missing.Any() ? PartitionDelta.Additive : PartitionDelta.Rebuild;
        }
        else
        {
            return PartitionDelta.Rebuild;
        }
    }

    public IEnumerable<string> PartitionTableNames(Table parent)
    {
        foreach (var partition in _ranges)
        {
            yield return $"{parent.Identifier.Name.ToLowerInvariant()}_{partition.Suffix.ToLowerInvariant()}";
        }

        yield return $"{parent.Identifier.Name.ToLowerInvariant()}_default";
    }

    /// <summary>
    /// Add another range partition with the name "{parent table name}_{suffix}"
    /// </summary>
    /// <param name="suffix">The suffix for the partition table name</param>
    /// <param name="from">The "from" value</param>
    /// <param name="to">The "to" value</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public RangePartitioning AddRange<T>(string suffix, T from, T to)
    {
        var partition = new RangePartition(suffix, from.FormatSqlValue(), to.FormatSqlValue());
        _ranges.Add(partition);

        return this;
    }

    void IPartitionStrategy.WriteCreateStatement(TextWriter writer, Table parent)
    {
        foreach (IPartition partition in _ranges)
        {
            partition.WriteCreateStatement(writer, parent);
            writer.WriteLine();
        }

        writer.WriteDefaultPartition(parent.Identifier);
    }

    internal async Task ReadPartitionsAsync(DbObjectName identifier, DbDataReader reader, CancellationToken ct)
    {
        var expectedDefaultName = identifier.Name + "_default";
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var partitionName = await reader.GetFieldValueAsync<string>(0, ct).ConfigureAwait(false);
            var expression = await reader.GetFieldValueAsync<string>(1, ct).ConfigureAwait(false);

            if (partitionName == expectedDefaultName)
            {
                HasExistingDefault = true;
            }
            else
            {
                var range = RangePartition.Parse(identifier, partitionName, expression);
                _ranges.Add(range);
            }
        }
    }
}
