using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Infrastructure.Storage;

/// <summary>
/// ttl per user (data-model 1.3, ADR 0006 section 6.3). A guest id (<c>guest-</c> prefix) ALWAYS gets Guest:DataTtl,
/// even when Chat:RetentionDays is unset and even when Guest:Enabled is false (leftover guest data still expires).
/// Everyone else: RetentionDays * 86400, or null (no ttl field) when unset. The prefix is used for retention only,
/// never for an authorization decision.
/// </summary>
public sealed class RetentionPolicy(int? retentionDays, TimeSpan guestDataTtl)
{
    public RetentionPolicy(IOptions<ChatLimitsOptions> chat, IOptions<GuestOptions> guest)
        : this(chat.Value.RetentionDays, guest.Value.DataTtl)
    {
    }

    public int? TtlSeconds(string userId) =>
        GuestOptions.IsGuestId(userId) ? GuestTtlSeconds : retentionDays is > 0 ? retentionDays.Value * 86400 : null;

    /// <summary>Ttl of the guest document and, after a quota patch, the value it is refreshed to.</summary>
    public int GuestTtlSeconds => (int)guestDataTtl.TotalSeconds;
}
