using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

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
    IDriveDuplicateReconciler? duplicateReconciler = null) : IGoogleDriveTransport, IGoogleDriveVersionReader
{
    public GoogleDriveSyncDiagnostics? Diagnostics { get; init; }
    private readonly GoogleDriveRestOptions options = options ?? new();
    private readonly IDriveDuplicateReconciler duplicateReconciler =
        duplicateReconciler ?? new DeterministicDriveDuplicateReconciler();

    public async Task<DriveWriteCondition?> ReadCloudVersionAsync(CancellationToken cancellationToken = default)
    {
        // Listing metadata also detects a deleted/replaced canonical file. Never request media here.
        var files = await ListFilesAsync(includeRevisions: false, cancellationToken);
        if (files.Any(file => string.IsNullOrWhiteSpace(file.VersionToken)))
            throw new DriveTransportException("Google Drive returned incomplete file metadata.");
        if (files.Length == 0) return null;
        var canonical = duplicateReconciler.SelectCanonical(files);
        return new(canonical.FileId, canonical.VersionToken);
    }

    public async Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default)
    {
        var files = await ListFilesAsync(includeRevisions: true, cancellationToken);
        if (files.Length == 0) return null;

        var canonical = duplicateReconciler.SelectCanonical(files);
        var before = await ReadMetadataAsync(canonical.FileId, cancellationToken);
        ObserveDownloadedFile(before);
        using var contentRequest = await AuthorizedAsync(HttpMethod.Get,
            $"{options.ApiBaseAddress}files/{Uri.EscapeDataString(canonical.FileId)}?alt=media", cancellationToken);
        using var contentResponse = await SendAsync(contentRequest, cancellationToken).ConfigureAwait(false);
        contentResponse.EnsureSuccessStatusCode();
        var envelope = await contentResponse.Content.ReadFromJsonAsync<SyncEnvelope>(cancellationToken)
            ?? throw new SyncSchemaException("The Google Drive sync file is empty.");
        var after = await ReadMetadataAsync(canonical.FileId, cancellationToken);
        if (before.Version != after.Version)
            throw new DriveConcurrencyException("The Google Drive sync file changed during download.");
        ObserveDownloadedFile(after);
        return new DriveSyncSnapshot(envelope, new DriveWriteCondition(canonical.FileId, after.Version),
            files.Where(file => file.FileId != canonical.FileId).ToArray());
    }

    private async Task<DriveFileCandidate[]> ListFilesAsync(bool includeRevisions, CancellationToken cancellationToken)
    {
        var fields = includeRevisions ? "id,name,version,modifiedTime,headRevisionId" : "id,version";
        var files = new List<DriveFileCandidate>();
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        do
        {
            var pageQuery = page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}";
            using var request = await AuthorizedAsync(HttpMethod.Get,
                $"{options.ApiBaseAddress}files?q=appProperties%20has%20%7B%20key%3D%27parkingHelperSync%27%20and%20value%3D%271%27%20%7D%20and%20trashed%3Dfalse&spaces=appDataFolder&fields=files({fields}),nextPageToken&pageSize=1000{pageQuery}",
                cancellationToken);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var list = await response.Content.ReadFromJsonAsync<DriveFileList>(cancellationToken).ConfigureAwait(false);
            if (list?.Files is null || list.Files.Any(file => string.IsNullOrWhiteSpace(file.Id)))
                throw new DriveTransportException("Google Drive returned incomplete file metadata.");
            files.AddRange(list.Files.Select(file => new DriveFileCandidate(file.Id!, file.Version, file.HeadRevisionId)));
            page = list.NextPageToken;
            if (!string.IsNullOrEmpty(page) && !visitedPages.Add(page))
                throw new DriveTransportException("Google Drive repeated a metadata page.");
        } while (!string.IsNullOrEmpty(page));
        if (includeRevisions && Diagnostics is { } diagnostics)
            diagnostics.Update(d => diagnostics.VerifyingCloud ? d with { VerifiedMatchingFiles = files.Count } : d with { MatchingFiles = files.Count });
        return files.OrderBy(file => file.FileId, StringComparer.Ordinal).ToArray();
    }

    public async Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition,
        CancellationToken cancellationToken = default)
    {
        SyncSchema.Validate(envelope);
        HttpRequestMessage request;
        if (string.IsNullOrWhiteSpace(condition.FileId))
        {
            request = await AuthorizedAsync(HttpMethod.Post,
                $"{options.UploadBaseAddress}files?uploadType=multipart&fields=id,name,version,modifiedTime",
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
            if (string.IsNullOrWhiteSpace(condition.VersionToken))
                throw new DriveTransportException("Google Drive sync has no observed file version. Try synchronizing again.");
            var current = await ReadMetadataAsync(condition.FileId, cancellationToken);
            if (current.Version != condition.VersionToken)
                throw new DriveConcurrencyException("The Google Drive sync file changed on another device.");
            // Drive v3 version is not an HTTP ETag. This check and PATCH are not atomic:
            // another writer can still intervene. Do not manufacture an If-Match header.
            request = await AuthorizedAsync(HttpMethod.Patch,
                $"{options.UploadBaseAddress}files/{Uri.EscapeDataString(condition.FileId)}?uploadType=media&fields=id,name,version,modifiedTime",
                cancellationToken);
            request.Content = JsonContent.Create(envelope);
        }
        using (request)
        using (var response = await SendAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                throw new DriveConcurrencyException("The Google Drive sync file changed on another device.");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var uploaded = string.IsNullOrWhiteSpace(body) ? null :
                JsonSerializer.Deserialize<DriveFileResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var id = uploaded?.Id ?? condition.FileId;
            if (string.IsNullOrWhiteSpace(id))
                throw new DriveTransportException("Google Drive did not return the uploaded sync file ID.");
            if (string.IsNullOrWhiteSpace(uploaded?.Version))
                uploaded = await ReadMetadataAsync(id, cancellationToken);
            if (uploaded.Version == condition.VersionToken)
                throw new DriveTransportException("Google Drive did not return a new sync file version.");
            Diagnostics?.Update(d => d with { UploadedFile = new(id, uploaded.Version, uploaded.ModifiedTime) });
            return new DriveUploadResult(new DriveWriteCondition(id, uploaded.Version));
        }
    }

    private void ObserveDownloadedFile(DriveFileResponse file)
    {
        if (Diagnostics is not { } diagnostics) return;
        var observed = new DriveDiagnosticFile(file.Id!, file.Version, file.ModifiedTime);
        diagnostics.Update(d => diagnostics.VerifyingCloud ? d with { VerifiedFile = observed } : d with { DownloadedFile = observed });
    }

    private async Task<DriveFileResponse> ReadMetadataAsync(string id, CancellationToken cancellationToken)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get,
            $"{options.ApiBaseAddress}files/{Uri.EscapeDataString(id)}?fields=id,name,version,modifiedTime",
            cancellationToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new DriveConcurrencyException("The Google Drive sync file was removed on another device.");
        response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadFromJsonAsync<DriveFileResponse>(cancellationToken);
        if (metadata?.Id != id || string.IsNullOrWhiteSpace(metadata.Version))
            throw new DriveTransportException("Google Drive did not return the sync file version. Try synchronizing again.");
        return metadata;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || tokens is not IGoogleDriveSession session)
            return response;
        response.Dispose();
        if (!await session.RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false))
            throw new GoogleDriveAuthenticationExpiredException();
        using var retry = await AuthorizedAsync(request.Method, request.RequestUri!.AbsoluteUri, cancellationToken);
        if (request.Content is not null)
        {
            retry.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in request.Content.Headers)
                retry.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var retried = await client.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        if (retried.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            retried.Dispose();
            await session.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
            throw new GoogleDriveAuthenticationExpiredException();
        }
        return retried;
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string uri,
        CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new GoogleDriveAuthenticationExpiredException();
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private sealed record DriveFileList(DriveFileResponse[]? Files, string? NextPageToken);
    private sealed record DriveFileResponse(string? Id, string? Name, string? Version, string? ModifiedTime, string? HeadRevisionId);
}

public sealed class DeterministicDriveDuplicateReconciler : IDriveDuplicateReconciler
{
    public DriveFileCandidate SelectCanonical(IReadOnlyList<DriveFileCandidate> candidates)
    {
        if (candidates.Count == 0) throw new ArgumentException("At least one Drive file is required.", nameof(candidates));
        return candidates.OrderBy(candidate => candidate.FileId, StringComparer.Ordinal).First();
    }
}

