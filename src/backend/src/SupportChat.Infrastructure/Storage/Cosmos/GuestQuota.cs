using System.Net;
using Microsoft.Azure.Cosmos;

namespace SupportChat.Infrastructure.Storage.Cosmos;

/// <summary>
/// Conditional patch of the guest document inside a transactional batch (ADR 0006 section 6.2, V3). The patch is the
/// quota gate: Increment under a FilterPredicate comparing two properties of the same document. Per-operation results:
/// 404 = guest document missing (guest ended), 412 = predicate false (quota used up); every other operation of the
/// failed batch reports 424 and nothing is written.
/// </summary>
internal static class GuestQuota
{
    public static IReadOnlyList<PatchOperation> PatchOperations(string counterPath, int ttlSeconds) =>
    [
        PatchOperation.Increment(counterPath, 1),
        PatchOperation.Set("/ttl", ttlSeconds), // Cosmos counts ttl from the last write; keep the guest document alive as long as its data
    ];

    public static TransactionalBatchPatchItemRequestOptions Predicate(string counterProperty, string maxProperty) =>
        new() { FilterPredicate = $"FROM c WHERE c.{counterProperty} < c.{maxProperty}" };

    /// <summary>Status of the guest patch operation of a failed batch (null when the response carries no such result).</summary>
    public static HttpStatusCode? PatchStatus(TransactionalBatchResponse response, int patchIndex) =>
        response.Count > patchIndex ? response[patchIndex].StatusCode : null;
}
