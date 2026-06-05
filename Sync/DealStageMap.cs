namespace HubSpotLeadSync;

/// <summary>
/// Translates between our canonical/portal stage names and HubSpot's internal deal stage ids,
/// and knows which stages count as Closed. Built from <see cref="HubSpotOptions.DealStages"/>
/// and <see cref="HubSpotOptions.ClosedDealStages"/> — see docs/hubspot-config-and-operations.md.
/// </summary>
public sealed class DealStageMap
{
    private readonly Dictionary<string, string> _toInternal;   // canonical -> HubSpot internal id
    private readonly HashSet<string> _closed;                  // internal ids that are Closed
    private readonly ILogger<DealStageMap> _log;

    public DealStageMap(HubSpotOptions options, ILogger<DealStageMap> log)
    {
        _log = log;
        _toInternal = new Dictionary<string, string>(options.DealStages, StringComparer.OrdinalIgnoreCase);
        _closed = new HashSet<string>(options.ClosedDealStages, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Map a portal/canonical stage to a HubSpot internal stage id. Unknown values pass through
    /// unchanged (with a one-line warning) so callers already sending internal ids still work.
    /// </summary>
    public string? ToInternalStageId(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return null;
        if (_toInternal.TryGetValue(stage, out var id)) return id;
        _log.LogWarning(
            "Deal stage '{Stage}' has no entry in HubSpot:DealStages; passing through as-is. " +
            "If this isn't a HubSpot internal stage id, the create/update will be rejected — add the mapping in config.",
            stage);
        return stage;
    }

    /// <summary>True if the given HubSpot internal stage id is a Closed stage (Won/Lost/Abandoned).</summary>
    public bool IsClosedStage(string? internalStageId) =>
        !string.IsNullOrWhiteSpace(internalStageId) && _closed.Contains(internalStageId!);
}
