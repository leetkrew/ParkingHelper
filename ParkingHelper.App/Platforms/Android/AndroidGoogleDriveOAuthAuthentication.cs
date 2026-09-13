using Android.App;
using Android.Content;
using Android.Gms.Auth.Api.Identity;
using Android.Gms.Auth.Api;
using Android.Gms.Common.Apis;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App;

public sealed class AndroidGoogleDriveOAuthAuthentication(
    IGoogleDriveOAuthConfiguration configuration) : IGoogleDriveAuthentication
{
    private const int AuthorizationRequestCode = 7401;
    private const string DriveScope = "https://www.googleapis.com/auth/drive.appdata";
    private readonly object gate = new();
    private string? accessToken;
    private TaskCompletionSource<Intent?>? pendingResult;

    public bool IsConnected => !string.IsNullOrWhiteSpace(accessToken);

    public async System.Threading.Tasks.Task<bool> ConnectAsync(
        System.Threading.CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
            ?? throw new InvalidOperationException("No Android activity is available for Google authorization.");
        var client = Identity.GetAuthorizationClient(activity);
        var request = AuthorizationRequest.InvokeBuilder()
            .SetRequestedScopes(new List<Scope> { new(DriveScope) })
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
        lock (gate) accessToken = result.AccessToken;
        return true;
    }

    public ValueTask<string?> GetAccessTokenAsync(
        System.Threading.CancellationToken cancellationToken = default)
    {
        lock (gate) return ValueTask.FromResult(accessToken);
    }

    public async System.Threading.Tasks.Task DisconnectAsync(
        System.Threading.CancellationToken cancellationToken = default)
    {
        string? token;
        lock (gate) { token = accessToken; accessToken = null; }
        if (string.IsNullOrWhiteSpace(token)) return;
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null) return;
        var client = Identity.GetAuthorizationClient(activity);
        var request = ClearTokenRequest.InvokeBuilder().SetToken(token).Build();
        await AwaitTask(client.ClearToken(request), cancellationToken).ConfigureAwait(true);
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
