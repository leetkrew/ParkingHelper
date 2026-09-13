using Microsoft.Maui.Networking;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

// The Google SDK/client credentials are intentionally supplied by a future platform
// adapter. This keeps the sync engine testable and prevents shipping fake credentials.
public sealed class UnconfiguredGoogleDriveAuthentication : IGoogleDriveAuthentication
{
    public bool IsConnected => false;
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new NotSupportedException("Google Drive authentication is not configured."));
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class UnconfiguredGoogleDriveTransport : IGoogleDriveTransport
{
    public Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<DriveSyncSnapshot?>(new NotSupportedException("Google Drive transport is not configured."));
    public Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DriveUploadResult>(new NotSupportedException("Google Drive transport is not configured."));
}

public sealed class MauiSyncNetworkStatus : ISyncNetworkStatus
{
    public bool IsOnline => Connectivity.Current.NetworkAccess is NetworkAccess.Internet or NetworkAccess.ConstrainedInternet;
}

public sealed record GoogleDriveRestOptions(
    string ApiBaseAddress = "https://www.googleapis.com/drive/v3/",
    string UploadBaseAddress = "https://www.googleapis.com/upload/drive/v3/",
    string FileName = "parkinghelper-sync-v1.json");

/// <summary>
/// Platform-neutral Google Drive REST transport. OAuth token acquisition remains
/// a native platform concern and is supplied through IGoogleDriveAccessTokenProvider.
/// </summary>
public sealed class GoogleDriveRestTransport(
    HttpClient client,
    IGoogleDriveAccessTokenProvider tokens,
    GoogleDriveRestOptions? options = null,
    IDriveDuplicateReconciler? duplicateReconciler = null) : IGoogleDriveTransport
{
    private readonly GoogleDriveRestOptions options = options ?? new();
    private readonly IDriveDuplicateReconciler duplicateReconciler =
        duplicateReconciler ?? new DeterministicDriveDuplicateReconciler();

    public async Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default)
    {
        var request = await AuthorizedAsync(HttpMethod.Get,
            $"{options.ApiBaseAddress}files?q=appProperties%20has%20%7B%20key%3D%27parkingHelperSync%27%20and%20value%3D%271%27%20%7D%20and%20trashed%3Dfalse&spaces=appDataFolder&fields=files(id,name,headRevisionId)",
            cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<DriveFileList>(cancellationToken).ConfigureAwait(false);
        var files = list?.Files?.Where(file => !string.IsNullOrWhiteSpace(file.Id))
            .Select(file => new DriveFileCandidate(file.Id!, file.ETag, file.HeadRevisionId))
            .OrderBy(file => file.FileId, StringComparer.Ordinal).ToArray() ?? [];
        if (files.Length == 0) return null;

        var canonical = duplicateReconciler.SelectCanonical(files);
        var contentRequest = await AuthorizedAsync(HttpMethod.Get,
            $"{options.ApiBaseAddress}files/{Uri.EscapeDataString(canonical.FileId)}?alt=media", cancellationToken);
        using var contentResponse = await client.SendAsync(contentRequest, cancellationToken).ConfigureAwait(false);
        contentResponse.EnsureSuccessStatusCode();
        var envelope = await contentResponse.Content.ReadFromJsonAsync<SyncEnvelope>(cancellationToken)
            ?? throw new SyncSchemaException("The Google Drive sync file is empty.");
        var etag = contentResponse.Headers.ETag?.Tag ?? canonical.ETag;
        return new DriveSyncSnapshot(envelope, new DriveWriteCondition(canonical.FileId, etag),
            files.Skip(1).ToArray());
    }

    public async Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition,
        CancellationToken cancellationToken = default)
    {
        SyncSchema.Validate(envelope);
        HttpRequestMessage request;
        if (string.IsNullOrWhiteSpace(condition.FileId))
        {
            request = await AuthorizedAsync(HttpMethod.Post,
                $"{options.UploadBaseAddress}files?uploadType=multipart&spaces=appDataFolder",
                cancellationToken);
            var metadata = JsonSerializer.Serialize(new
            {
                name = options.FileName,
                mimeType = "application/json",
                parents = new[] { "appDataFolder" },
                appProperties = new Dictionary<string, string> { ["parkingHelperSync"] = "1" }
            });
            var boundary = "parking-helper-sync";
            var multipart = new MultipartContent("related", boundary);
            multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
            multipart.Add(new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json"));
            request.Content = multipart;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(condition.ETag))
                throw new NotSupportedException("Google Drive did not provide a version tag for conditional sync.");
            request = await AuthorizedAsync(HttpMethod.Patch,
                $"{options.UploadBaseAddress}files/{Uri.EscapeDataString(condition.FileId)}?uploadType=media",
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(condition.ETag))
                request.Headers.IfMatch.Add(new EntityTagHeaderValue(condition.ETag));
            request.Content = JsonContent.Create(envelope);
        }
        using (request)
        using (var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                throw new DriveConcurrencyException("The Google Drive sync file changed on another device.");
            response.EnsureSuccessStatusCode();
            var id = condition.FileId;
            if (string.IsNullOrWhiteSpace(id))
            {
                var created = await response.Content.ReadFromJsonAsync<DriveFileResponse>(cancellationToken);
                id = created?.Id;
            }
            return new DriveUploadResult(new DriveWriteCondition(id,
                response.Headers.ETag?.Tag ?? condition.ETag));
        }
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string uri,
        CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new NotSupportedException("Google Drive authentication is not configured.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private sealed record DriveFileList(DriveFileResponse[]? Files);
    private sealed record DriveFileResponse(string? Id, string? ETag, string? HeadRevisionId);
}

public sealed class DeterministicDriveDuplicateReconciler : IDriveDuplicateReconciler
{
    public DriveFileCandidate SelectCanonical(IReadOnlyList<DriveFileCandidate> candidates)
    {
        if (candidates.Count == 0) throw new ArgumentException("At least one Drive file is required.", nameof(candidates));
        return candidates.OrderBy(candidate => candidate.FileId, StringComparer.Ordinal).First();
    }
}

public sealed class SynchronizationTrigger(
    GoogleDriveSynchronizationService synchronization,
    IGoogleDriveAuthentication authentication,
    ISyncNetworkStatus network) : ISynchronizationTrigger
{
    private readonly object gate = new();
    private CancellationTokenSource? pending;

    public void RequestSync() => Schedule(TimeSpan.FromSeconds(2));
    public void RequestResumeSync() => Schedule(TimeSpan.Zero);

    private void Schedule(TimeSpan delay)
    {
        if (!network.IsOnline || !authentication.IsConnected) return;
        lock (gate)
        {
            pending?.Cancel();
            pending = new CancellationTokenSource();
            _ = RunAsync(pending, delay);
        }
    }

    private async Task RunAsync(CancellationTokenSource source, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, source.Token).ConfigureAwait(false);
            await synchronization.SynchronizeAsync(source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception) when (source.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, source)) pending = null;
            }
            source.Dispose();
        }
    }
}
