using Android.App;
using Android.Content;
using Android.Gms.Auth.Api.Identity;
using Android.Gms.Auth.Api;
using Android.Gms.Common.Apis;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App;

public sealed class AndroidGoogleDriveOAuthAuthentication(
    IGoogleDriveOAuthConfiguration configuration,
    HttpClient http) : IGoogleDriveSession
{
    private const int AuthorizationRequestCode = 7401;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private GoogleDriveAccount? account;
    private readonly object gate = new();
    private string? accessToken;
    private readonly GoogleDriveAndroidSessionStorage savedSession = new(SecureStorage.Default);
    private bool loaded;
    private TaskCompletionSource<Intent?>? pendingResult;

    public bool IsConnected => !string.IsNullOrWhiteSpace(accessToken);
    public GoogleDriveAccount? Account => IsConnected ? account : null;
    public async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (loaded) return;
            var saved = await savedSession.ReadAsync();
            lock (gate) { accessToken = saved?.AccessToken; account = saved?.Account; loaded = true; }
        }
        finally { sessionGate.Release(); }
        // GIS owns refresh credentials. Restore identity before attempting silent renewal,
        // so a network failure leaves the durable account connection intact.
        if (IsConnected) await RefreshAccessTokenAsync(cancellationToken);
    }

    public async System.Threading.Tasks.Task<bool> ConnectAsync(
        System.Threading.CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            ValidateConfiguration();
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                ?? throw new InvalidOperationException("No Android activity is available for Google authorization.");
            var client = Identity.GetAuthorizationClient(activity);
            var request = AuthorizationRequest.InvokeBuilder()
                .SetRequestedScopes(GoogleDriveIdentity.Scopes.Select(scope => new Scope(scope)).ToList())
                .SetPrompt(AuthorizationRequest.Prompt.SelectAccount)
                .SetOptOutIncludingGrantedScopes(true)
                .Build();
            var result = await AwaitTask<AuthorizationResult>(
                client.Authorize(request), cancellationToken).ConfigureAwait(true);
            if (result.HasResolution)
            {
                var pending = result.PendingIntent
                    ?? throw new InvalidOperationException("Google authorization did not provide a resolution.");
                var intent = await ResolveAsync(activity, pending, cancellationToken).ConfigureAwait(true);
                result = client.GetAuthorizationResultFromIntent(intent)
                    ?? throw new InvalidOperationException("Google authorization returned no result.");
            }
            if (string.IsNullOrWhiteSpace(result.AccessToken))
                throw new InvalidOperationException("Google authorization did not return an access token.");
            if (result.GrantedScopes?.Contains(GoogleDriveIdentity.DriveScope) != true)
                throw new InvalidOperationException("Google Drive permission was not granted.");
            // This conversion is supplied by the installed GIS binding; retain only display fields.
            var selected = result.ToGoogleSignInAccount();
            var identity = selected is null ? null : new GoogleDriveAccount(selected.DisplayName, selected.Email);
            if (string.IsNullOrWhiteSpace(identity?.Email) || string.IsNullOrWhiteSpace(identity.Name))
            {
                var fetched = await GoogleDriveIdentity.ReadAsync(http, result.AccessToken, cancellationToken);
                identity = new GoogleDriveAccount(fetched?.Name ?? identity?.Name, fetched?.Email ?? identity?.Email);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await savedSession.SaveAsync(result.AccessToken, identity);
            lock (gate) { accessToken = result.AccessToken; account = identity; loaded = true; }
            return true;
        }
        catch (ApiException error) when (error.StatusCode == 16) { return false; }
        finally { sessionGate.Release(); }
    }

    public ValueTask<string?> GetAccessTokenAsync(
        System.Threading.CancellationToken cancellationToken = default)
    {
        lock (gate) return ValueTask.FromResult(accessToken);
    }

    public async System.Threading.Tasks.Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            var previous = accessToken;
            if (previous is null) return false;
            var client = Identity.GetAuthorizationClient(Android.App.Application.Context);
            await AwaitTask(client.ClearToken(ClearTokenRequest.InvokeBuilder().SetToken(previous).Build()), cancellationToken);
            var builder = AuthorizationRequest.InvokeBuilder()
                .SetRequestedScopes(GoogleDriveIdentity.Scopes.Select(scope => new Scope(scope)).ToList())
                .SetOptOutIncludingGrantedScopes(true);
            // Never silently switch to another account during a sync.
            if (string.IsNullOrWhiteSpace(account?.Email))
            {
                await ClearSessionAsync(cancellationToken);
                return false;
            }
            builder.SetAccount(new Android.Accounts.Account(account.Email, "com.google"));
            var result = await AwaitTask<AuthorizationResult>(client.Authorize(builder.Build()), cancellationToken);
            if (result.HasResolution)
            {
                await ClearSessionAsync(cancellationToken);
                return false;
            }
            if (string.IsNullOrWhiteSpace(result.AccessToken)
                || result.GrantedScopes?.Contains(GoogleDriveIdentity.DriveScope) != true)
                throw new InvalidOperationException("Google authorization returned an incomplete session.");
            await savedSession.SaveAsync(result.AccessToken, account);
            lock (gate) accessToken = result.AccessToken;
            return true;
        }
        catch (ApiException error) when (error.StatusCode == 4)
        {
            await ClearSessionAsync(cancellationToken);
            return false;
        }
        finally { sessionGate.Release(); }
    }

    public async System.Threading.Tasks.Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            string? previous;
            GoogleDriveAccount? previousAccount;
            lock (gate) { previous = accessToken; previousAccount = account; }
            await ClearSessionAsync(cancellationToken);
            if (previous is null) return;
            var revoked = false;
            var client = Identity.GetAuthorizationClient(Android.App.Application.Context);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                if (!string.IsNullOrWhiteSpace(previousAccount?.Email))
                {
                    var request = RevokeAccessRequest.InvokeBuilder()
                        .SetAccount(new Android.Accounts.Account(previousAccount.Email, "com.google"))
                        .SetScopes(GoogleDriveIdentity.Scopes.Select(scope => new Scope(scope)).ToList()).Build();
                    await AwaitTask(client.RevokeAccess(request), timeout.Token);
                    revoked = true;
                }
                else revoked = await GoogleDriveIdentity.RevokeAsync(http, previous, timeout.Token);
            }
            catch (Exception) { revoked = false; }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await AwaitTask(client.ClearToken(ClearTokenRequest.InvokeBuilder().SetToken(previous).Build()), timeout.Token); }
                catch (Exception) { /* Local memory is already cleared; Connect always opens account selection. */ }
            }
            if (!revoked)
                throw new GoogleDriveDisconnectException("Disconnected locally. Google authorization could not be revoked; remove Parking Helper access in your Google Account.");
        }
        finally { sessionGate.Release(); }
    }

    public System.Threading.Tasks.Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        savedSession.Clear();
        lock (gate)
        {
            loaded = true;
            accessToken = null;
            account = null;
            pendingResult?.TrySetCanceled();
            pendingResult = null;
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public void CompleteAuthorization(Intent? data)
    {
        lock (gate)
        {
            pendingResult?.TrySetResult(data);
            pendingResult = null;
        }
    }

    private async System.Threading.Tasks.Task<Intent> ResolveAsync(
        Activity activity, PendingIntent pending, System.Threading.CancellationToken cancellationToken)
    {
        var completion = new System.Threading.Tasks.TaskCompletionSource<Intent?>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) pendingResult = completion;
        try
        {
            activity.StartIntentSenderForResult(
                pending.IntentSender, AuthorizationRequestCode, null, 0, 0, 0);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(true)
                ?? throw new OperationCanceledException("Google authorization was cancelled.", cancellationToken);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pendingResult, completion)) pendingResult = null;
            }
        }
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(configuration.ClientId)
            || string.IsNullOrWhiteSpace(configuration.PackageName))
            throw new NotSupportedException("Android Google OAuth client configuration is missing.");
    }

    private static System.Threading.Tasks.Task<T> AwaitTask<T>(
        Android.Gms.Tasks.Task task, System.Threading.CancellationToken cancellationToken)
        where T : Java.Lang.Object
    {
        var completion = new System.Threading.Tasks.TaskCompletionSource<T>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        task.AddOnSuccessListener(new SuccessListener<T>(completion));
        task.AddOnFailureListener(new FailureListener<T>(completion));
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    private static System.Threading.Tasks.Task AwaitTask(
        Android.Gms.Tasks.Task task, System.Threading.CancellationToken cancellationToken)
    {
        var completion = new System.Threading.Tasks.TaskCompletionSource(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        task.AddOnSuccessListener(new VoidSuccessListener(completion));
        task.AddOnFailureListener(new VoidFailureListener(completion));
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    private sealed class SuccessListener<T>(TaskCompletionSource<T> completion)
        : Java.Lang.Object, Android.Gms.Tasks.IOnSuccessListener where T : Java.Lang.Object
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            if (result is T value) completion.TrySetResult(value);
            else completion.TrySetException(new InvalidOperationException("Google authorization returned an invalid result."));
        }
    }

    private sealed class FailureListener<T>(TaskCompletionSource<T> completion)
        : Java.Lang.Object, Android.Gms.Tasks.IOnFailureListener where T : Java.Lang.Object
    {
        public void OnFailure(Java.Lang.Exception? error) =>
            completion.TrySetException((Exception?)error ?? new InvalidOperationException("Google authorization failed."));
    }

    private sealed class VoidSuccessListener(TaskCompletionSource completion)
        : Java.Lang.Object, Android.Gms.Tasks.IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result) => completion.TrySetResult();
    }

    private sealed class VoidFailureListener(TaskCompletionSource completion)
        : Java.Lang.Object, Android.Gms.Tasks.IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception? error) =>
            completion.TrySetException((Exception?)error ?? new InvalidOperationException("Google authorization failed."));
    }
}
