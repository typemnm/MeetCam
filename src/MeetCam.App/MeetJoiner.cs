using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetCam;

internal enum MeetSignal
{
    NeedLogin,
    SigningIn,
    NeedVerification,
    LoginRejected,
    LoginBlocked,
    Asked,
    WaitingApproval,
    Admitted,
    CameraOn,
    Denied,
    Invalid,
    Failed,
}

// Opens a dedicated Chrome window, asks to join, and leaves the camera off until the host admits us.
internal sealed class MeetJoiner
{
    private const int Port = 9333;
    private const string Buttons = "button, [role=\"button\"]";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private int _nextId = 1;
    private ClientWebSocket? _browser;
    private ClientWebSocket? _page;
    private Process? _chrome;
    private bool _inCall;

    public async Task RunAsync(
        string url,
        string? account,
        string? password,
        Func<MeetSignal, Task<bool>> onSignal,
        CancellationToken cancellationToken)
    {
        if (!MeetingPlan.TryNormalize(url, out url))
            throw new InvalidOperationException("Meet 링크가 아닙니다.");
        account = account?.Trim() ?? "";
        password ??= "";

        try
        {
            if (!await PingAsync(cancellationToken).ConfigureAwait(false))
                Launch();
            await WaitForBrowserAsync(cancellationToken).ConfigureAwait(false);
            await ConnectAsync(url, cancellationToken).ConfigureAwait(false);
            await Task.Delay(1200, cancellationToken).ConfigureAwait(false);

            var prepared = false;
            var joinClicks = 0;
            var nextJoin = DateTime.UtcNow;
            var returns = 0;
            var nextReturn = DateTime.MinValue;
            var login = new LoginProgress();
            var deadline = DateTime.UtcNow.AddHours(3);
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await DismissDialogAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (snapshot == null)
                {
                    await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!MeetPage.IsAccountsPage(snapshot.Href) && !MeetPage.IsThisMeeting(snapshot.Href, url))
                {
                    if (returns >= 8)
                    {
                        await onSignal(MeetSignal.Failed).ConfigureAwait(false);
                        return;
                    }

                    if (DateTime.UtcNow >= nextReturn)
                    {
                        returns++;
                        nextReturn = DateTime.UtcNow.AddSeconds(5);
                        await SendAsync(_page!, "Page.navigate", new Dictionary<string, object?> { ["url"] = url }, cancellationToken).ConfigureAwait(false);
                        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var stage = MeetPage.Classify(snapshot.Href, snapshot.Body, snapshot.Controls);
                if (stage == MeetStage.Login)
                {
                    await HandleLoginAsync(account, password, snapshot, login, onSignal, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (stage == MeetStage.Invalid)
                {
                    await onSignal(MeetSignal.Invalid).ConfigureAwait(false);
                    return;
                }
                else if (stage == MeetStage.Denied)
                {
                    await onSignal(MeetSignal.Denied).ConfigureAwait(false);
                    return;
                }
                else if (stage == MeetStage.Lobby)
                {
                    if (!prepared)
                    {
                        await SetMicrophoneOffAsync(snapshot, cancellationToken).ConfigureAwait(false);
                        await SetCameraAsync(snapshot, wantOff: true, cancellationToken).ConfigureAwait(false);
                        await TrySelectMeetCamAsync(snapshot, cancellationToken).ConfigureAwait(false);
                        prepared = true;
                    }

                    if (joinClicks < 3 && DateTime.UtcNow >= nextJoin)
                    {
                        var again = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
                        var join = again == null ? -1 : MeetPage.FindJoin(again.Controls);
                        if (join >= 0 && await ClickIndexAsync(join, cancellationToken).ConfigureAwait(false))
                        {
                            joinClicks++;
                            nextJoin = DateTime.UtcNow.AddSeconds(4);
                            await onSignal(MeetSignal.Asked).ConfigureAwait(false);
                        }
                    }
                }
                else if (stage == MeetStage.Waiting)
                {
                    await onSignal(MeetSignal.WaitingApproval).ConfigureAwait(false);
                }
                else if (stage == MeetStage.InCall)
                {
                    _inCall = true;
                    var started = await onSignal(MeetSignal.Admitted).ConfigureAwait(false);
                    if (!started)
                    {
                        await onSignal(MeetSignal.Failed).ConfigureAwait(false);
                        return;
                    }

                    await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                    await SetMicrophoneOffAsync(null, cancellationToken).ConfigureAwait(false);
                    var turnedOn = await SetCameraAsync(null, wantOff: false, cancellationToken).ConfigureAwait(false);
                    await onSignal(turnedOn ? MeetSignal.CameraOn : MeetSignal.Failed).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
                await onSignal(MeetSignal.Failed).ConfigureAwait(false);
        }
        finally
        {
            if (!_inCall)
                await CloseBrowserQuietlyAsync().ConfigureAwait(false);
            DisposeSockets();
        }
    }

    private void Launch()
    {
        var browser = FindBrowser();
        if (browser == null)
            throw new InvalidOperationException("Chrome 또는 Edge를 찾지 못했습니다.");

        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetCam",
            "chrome-profile");
        Directory.CreateDirectory(profile);
        var info = new ProcessStartInfo(browser) { UseShellExecute = false };
        info.ArgumentList.Add("--user-data-dir=" + profile);
        info.ArgumentList.Add("--remote-debugging-port=" + Port);
        info.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        info.ArgumentList.Add("--remote-allow-origins=*");
        info.ArgumentList.Add("--no-first-run");
        info.ArgumentList.Add("--no-default-browser-check");
        info.ArgumentList.Add("--disable-session-crashed-bubble");
        info.ArgumentList.Add("--use-fake-ui-for-media-stream");
        info.ArgumentList.Add("--new-window");
        info.ArgumentList.Add("about:blank");
        _chrome = Process.Start(info);
    }

    private static string? FindBrowser()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task WaitForBrowserAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await PingAsync(cancellationToken).ConfigureAwait(false))
                return;
            if (_chrome is { HasExited: true })
                break;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("회의 브라우저를 열지 못했습니다.");
    }

    private static async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync($"http://127.0.0.1:{Port}/json/version", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task ConnectAsync(string url, CancellationToken cancellationToken)
    {
        var version = await Http.GetStringAsync(new Uri($"http://127.0.0.1:{Port}/json/version"), cancellationToken).ConfigureAwait(false);
        using var versionJson = JsonDocument.Parse(version);
        var browserWs = versionJson.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
            ?? throw new InvalidOperationException("회의 브라우저를 열지 못했습니다.");
        _browser = await OpenSocketAsync(browserWs, cancellationToken).ConfigureAwait(false);

        var listJson = await Http.GetStringAsync(new Uri($"http://127.0.0.1:{Port}/json/list"), cancellationToken).ConfigureAwait(false);
        using var list = JsonDocument.Parse(listJson);
        string? pageWs = null;
        foreach (var item in list.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "page")
                continue;
            if (item.TryGetProperty("webSocketDebuggerUrl", out var socket))
            {
                pageWs = socket.GetString();
                break;
            }
        }

        if (string.IsNullOrEmpty(pageWs))
            throw new InvalidOperationException("회의 페이지를 열지 못했습니다.");
        _page = await OpenSocketAsync(pageWs, cancellationToken).ConfigureAwait(false);
        await SendAsync(_page, "Page.navigate", new Dictionary<string, object?> { ["url"] = url }, cancellationToken).ConfigureAwait(false);
        await SendAsync(_page, "Page.bringToFront", new Dictionary<string, object?>(), cancellationToken, ignoreError: true).ConfigureAwait(false);
    }

    private async Task<Snapshot?> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var expression = """
            (() => {
              const nodes = [...document.querySelectorAll('button, [role="button"]')];
              return JSON.stringify({
                href: location.href,
                body: (document.body && document.body.innerText || '').slice(0, 3000),
                controls: nodes.slice(0, 80).map((el, index) => ({
                  index,
                  text: (el.innerText || '').trim().slice(0, 80),
                  label: (el.getAttribute('aria-label') || '').trim().slice(0, 140),
                  muted: el.getAttribute('data-is-muted') || ''
                }))
              });
            })()
            """;
        var json = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var dto = JsonSerializer.Deserialize<Snapshot>(json, JsonOptions);
        return dto;
    }

    private async Task SetMicrophoneOffAsync(Snapshot? snapshot, CancellationToken cancellationToken)
    {
        snapshot ??= await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
            return;
        var index = MeetPage.FindMicrophone(snapshot.Controls);
        if (index < 0)
            return;
        var control = snapshot.Controls.First(item => item.Index == index);
        if (MeetPage.CameraIsOff(control) == false)
            await ClickIndexAsync(index, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SetCameraAsync(Snapshot? snapshot, bool wantOff, CancellationToken cancellationToken)
    {
        snapshot ??= await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
            return false;
        var index = MeetPage.FindCamera(snapshot.Controls);
        if (index < 0)
            return false;
        var control = snapshot.Controls.First(item => item.Index == index);
        var off = MeetPage.CameraIsOff(control);
        if (wantOff)
        {
            if (off == false)
                return await ClickIndexAsync(index, cancellationToken).ConfigureAwait(false);
            return off == true;
        }

        if (off == false)
            return true;
        if (off == true)
            return await ClickIndexAsync(index, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task TrySelectMeetCamAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        var options = MeetPage.FindCameraOptions(snapshot.Controls);
        if (options >= 0)
        {
            await ClickIndexAsync(options, cancellationToken).ConfigureAwait(false);
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        var expression = """
            (() => {
              const el = [...document.querySelectorAll('[role="option"], [role="menuitem"], li, span, button')]
                .find(node => /meetcam/i.test(node.innerText || '') && (node.innerText || '').trim().length < 80);
              if (!el) return '';
              el.scrollIntoView({block: 'center', inline: 'center'});
              const rect = el.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) return '';
              return JSON.stringify({x: rect.x + rect.width / 2, y: rect.y + rect.height / 2});
            })()
            """;
        var json = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        var point = ParsePoint(json);
        if (point != null)
            await MouseClickAsync(point.Value.X, point.Value.Y, cancellationToken).ConfigureAwait(false);
    }

    private async Task DismissDialogAsync(CancellationToken cancellationToken)
    {
        var expression = $$"""
            (() => {
              const stay = [{{MeetPage.StayPhraseList()}}];
              const ack = [{{MeetPage.AckPhraseList()}}];
              const match = (node, phrases) => phrases.includes((node.innerText || '').trim());
              const buttons = [...document.querySelectorAll('button, [role="button"]')];
              let button = buttons.find(node => match(node, stay));
              if (!button && location.hostname === 'meet.google.com') {
                const dialog = document.querySelector('[role="dialog"]');
                if (dialog)
                  button = [...dialog.querySelectorAll('button, [role="button"]')].find(node => match(node, ack));
              }
              if (!button) return '';
              button.scrollIntoView({block: 'center', inline: 'center'});
              const rect = button.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) return '';
              return JSON.stringify({x: rect.x + rect.width / 2, y: rect.y + rect.height / 2});
            })()
            """;
        var json = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        var point = ParsePoint(json);
        if (point != null)
            await MouseClickAsync(point.Value.X, point.Value.Y, cancellationToken).ConfigureAwait(false);
    }

    // The saved password is submitted once. A phone check stays up until the user answers it.
    private async Task HandleLoginAsync(
        string account,
        string password,
        Snapshot snapshot,
        LoginProgress login,
        Func<MeetSignal, Task<bool>> onSignal,
        CancellationToken cancellationToken)
    {
        if (!login.Raised)
        {
            login.Raised = true;
            await SendAsync(_page!, "Page.bringToFront", new Dictionary<string, object?>(), cancellationToken, ignoreError: true).ConfigureAwait(false);
        }

        if (MeetPage.IsAccountsPage(snapshot.Href))
        {
            await HandleAccountsAsync(account, password, snapshot, login, onSignal, cancellationToken).ConfigureAwait(false);
            return;
        }

        await onSignal(account.Length == 0 ? MeetSignal.NeedLogin : MeetSignal.SigningIn).ConfigureAwait(false);
        if (login.SignInClicks >= 3 || DateTime.UtcNow < login.NextSignIn)
            return;
        if (await ClickSignInAsync(cancellationToken).ConfigureAwait(false))
        {
            login.SignInClicks++;
            login.NextSignIn = DateTime.UtcNow.AddSeconds(6);
        }
    }

    private async Task HandleAccountsAsync(
        string account,
        string password,
        Snapshot snapshot,
        LoginProgress login,
        Func<MeetSignal, Task<bool>> onSignal,
        CancellationToken cancellationToken)
    {
        if (MeetPage.IsBrowserBlocked(snapshot.Body))
        {
            login.EmailSent = true;
            login.PasswordSent = true;
            await onSignal(MeetSignal.LoginBlocked).ConfigureAwait(false);
            return;
        }

        if (login.PasswordSent && MeetPage.IsPasswordRejected(snapshot.Body))
        {
            await onSignal(MeetSignal.LoginRejected).ConfigureAwait(false);
            return;
        }

        if (MeetPage.IsPasswordPage(snapshot.Href))
        {
            await HandlePasswordPageAsync(password, login, onSignal, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (MeetPage.IsPasskeyOffer(snapshot.Href, snapshot.Body))
        {
            if (login.PasskeySkips < 3 && DateTime.UtcNow >= login.NextPasskey
                && await ClickPasskeySkipAsync(cancellationToken).ConfigureAwait(false))
            {
                login.PasskeySkips++;
                login.NextPasskey = DateTime.UtcNow.AddSeconds(2);
            }

            await onSignal(MeetSignal.SigningIn).ConfigureAwait(false);
            return;
        }

        if (MeetPage.IsVerification(snapshot.Href, snapshot.Body))
        {
            if (!login.VerificationRaised)
            {
                login.VerificationRaised = true;
                await SendAsync(_page!, "Page.bringToFront", new Dictionary<string, object?>(), cancellationToken, ignoreError: true).ConfigureAwait(false);
            }

            await onSignal(MeetSignal.NeedVerification).ConfigureAwait(false);
            await AdvanceEnteredCodeAsync(login, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!login.EmailSent && account.Length > 0 && DateTime.UtcNow >= login.NextAction
            && await FieldStateAsync("input[type=\"email\"], input[name=\"identifier\"], input#identifierId", cancellationToken).ConfigureAwait(false) != "missing")
        {
            var emailResult = await FillAndNextAsync(
                "input[type=\"email\"], input[name=\"identifier\"], input#identifierId",
                account,
                "identifierNext",
                cancellationToken).ConfigureAwait(false);
            if (emailResult == FillResult.Done)
            {
                login.EmailSent = true;
                login.NextAction = DateTime.UtcNow.AddSeconds(2);
                await onSignal(MeetSignal.SigningIn).ConfigureAwait(false);
                return;
            }

            if (emailResult == FillResult.Failed)
            {
                login.EmailAttempts++;
                login.NextAction = DateTime.UtcNow.AddSeconds(2);
                if (login.EmailAttempts >= 2)
                    login.EmailSent = true;
                await onSignal(MeetSignal.SigningIn).ConfigureAwait(false);
                return;
            }
        }

        if (account.Length > 0 && login.AccountClicks < 2 && DateTime.UtcNow >= login.NextAccount)
        {
            var picked = await ClickMatchingAccountAsync(account, cancellationToken).ConfigureAwait(false);
            if (picked.Length > 0)
            {
                login.AccountClicks++;
                login.NextAccount = DateTime.UtcNow.AddSeconds(4);
                if (picked == "account")
                    login.EmailSent = true;
                await onSignal(MeetSignal.SigningIn).ConfigureAwait(false);
                return;
            }
        }

        if (login.EmailSent && !login.PasswordSent && login.ConfirmClicks < 2
            && DateTime.UtcNow >= login.NextAction && MeetPage.IsConfirmPage(snapshot.Href)
            && await ClickElementIdAsync("identifierNext", cancellationToken).ConfigureAwait(false))
        {
            login.ConfirmClicks++;
            login.NextAction = DateTime.UtcNow.AddSeconds(2);
            await onSignal(MeetSignal.SigningIn).ConfigureAwait(false);
            return;
        }

        if (account.Length == 0)
            await onSignal(MeetSignal.NeedLogin).ConfigureAwait(false);
        else if (login.PasswordSent)
            await onSignal(MeetSignal.NeedVerification).ConfigureAwait(false);
        else
            await onSignal(MeetSignal.SigningIn).ConfigureAwait(false);
    }

    private async Task HandlePasswordPageAsync(
        string password,
        LoginProgress login,
        Func<MeetSignal, Task<bool>> onSignal,
        CancellationToken cancellationToken)
    {
        if (login.PasswordAttempts >= 2)
        {
            login.PasswordSent = true;
            await onSignal(MeetSignal.NeedLogin).ConfigureAwait(false);
            return;
        }

        if (password.Length == 0 || DateTime.UtcNow < login.NextAction)
        {
            await onSignal(password.Length == 0 ? MeetSignal.NeedLogin : MeetSignal.SigningIn).ConfigureAwait(false);
            return;
        }

        var state = await FieldStateAsync("input[type=\"password\"]", cancellationToken).ConfigureAwait(false);
        if (state == "missing")
        {
            await onSignal(MeetSignal.SigningIn).ConfigureAwait(false);
            return;
        }

        login.PasswordAttempts++;
        login.NextAction = DateTime.UtcNow.AddSeconds(3);
        var clicked = await FillAndNextAsync("input[type=\"password\"]", password, "passwordNext", cancellationToken).ConfigureAwait(false) == FillResult.Done;
        if (clicked)
            login.PasswordSent = true;
        await onSignal(MeetSignal.SigningIn).ConfigureAwait(false);
    }

    private async Task AdvanceEnteredCodeAsync(LoginProgress login, CancellationToken cancellationToken)
    {
        if (login.CodeAdvances >= 2 || DateTime.UtcNow < login.NextCode)
            return;
        var ready = await EvaluateAsync(
            """
            (() => {
              const path = location.pathname.toLowerCase();
              if (path.includes('/pwd') || path.includes('password')) return 'no';
              const input = document.querySelector('input[autocomplete="one-time-code"], input[name="Pin"], input[name="idvPin"], input[type="tel"]');
              if (!input || (input.value || '').trim().length < 4) return 'no';
              return 'ready';
            })()
            """,
            cancellationToken).ConfigureAwait(false);
        if (ready != "ready")
            return;
        if (await ClickCodeNextAsync(cancellationToken).ConfigureAwait(false))
        {
            login.CodeAdvances++;
            login.NextCode = DateTime.UtcNow.AddSeconds(3);
        }
    }

    private async Task<string> FieldStateAsync(string selector, CancellationToken cancellationToken)
    {
        var selectorLiteral = JsonSerializer.Serialize(selector);
        var expression = $$"""
            (() => {
              const nodes = [...document.querySelectorAll({{selectorLiteral}})];
              const el = nodes.find(node => {
                const rect = node.getBoundingClientRect();
                const style = getComputedStyle(node);
                return rect.width > 2 && rect.height > 2 && style.visibility !== 'hidden' && style.display !== 'none';
              });
              if (!el) return 'missing';
              return (el.value || '').length > 0 ? 'filled' : 'empty';
            })()
            """;
        return await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false) ?? "missing";
    }

    private async Task<FillResult> FillAndNextAsync(string selector, string value, string nextId, CancellationToken cancellationToken)
    {
        var filled = await EvaluateAsync(FillExpression(selector, value), cancellationToken).ConfigureAwait(false);
        if (filled == "missing")
            return FillResult.Missing;
        if (filled != "ok")
            return FillResult.Failed;
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        return await ClickElementIdAsync(nextId, cancellationToken).ConfigureAwait(false) ? FillResult.Done : FillResult.Failed;
    }

    private static string FillExpression(string selector, string value)
    {
        var selectorLiteral = JsonSerializer.Serialize(selector);
        var valueLiteral = JsonSerializer.Serialize(value);
        return $$"""
            (() => {
              const nodes = [...document.querySelectorAll({{selectorLiteral}})];
              const el = nodes.find(node => {
                const rect = node.getBoundingClientRect();
                const style = getComputedStyle(node);
                return rect.width > 2 && rect.height > 2 && style.visibility !== 'hidden' && style.display !== 'none' && !node.disabled;
              });
              if (!el) return 'missing';
              el.focus();
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
              if (!setter) return 'missing';
              setter.call(el, {{valueLiteral}});
              el.dispatchEvent(new Event('input', {bubbles: true}));
              el.dispatchEvent(new Event('change', {bubbles: true}));
              return 'ok';
            })()
            """;
    }

    private async Task<bool> ClickElementIdAsync(string elementId, CancellationToken cancellationToken)
    {
        var idLiteral = JsonSerializer.Serialize(elementId);
        var expression = $$"""
            (() => {
              const root = document.getElementById({{idLiteral}});
              let el = root ? (root.querySelector('button') || root) : null;
              if (!el) {
                el = [...document.querySelectorAll('button')].find(node => {
                  const text = (node.innerText || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  return text === 'next' || text === '다음';
                });
              }
              if (!el || el.disabled || el.getAttribute('aria-disabled') === 'true') return '';
              el.scrollIntoView({block: 'center', inline: 'center'});
              const rect = el.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) return '';
              return JSON.stringify({x: rect.x + rect.width / 2, y: rect.y + rect.height / 2});
            })()
            """;
        var point = ParsePoint(await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false));
        if (point == null)
            return false;
        await MouseClickAsync(point.Value.X, point.Value.Y, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ClickPasskeySkipAsync(CancellationToken cancellationToken)
    {
        var expression = $$"""
            (() => {
              const skip = [{{MeetPage.PasskeySkipList()}}];
              const el = [...document.querySelectorAll('button, [role="button"]')].find(node => {
                const text = (node.innerText || '').replace(/\s+/g, ' ').trim();
                return skip.some(phrase => phrase.toLowerCase() === text.toLowerCase());
              });
              if (!el || el.disabled || el.getAttribute('aria-disabled') === 'true') return '';
              el.scrollIntoView({block: 'center', inline: 'center'});
              const rect = el.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) return '';
              return JSON.stringify({x: rect.x + rect.width / 2, y: rect.y + rect.height / 2});
            })()
            """;
        var point = ParsePoint(await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false));
        if (point == null)
            return false;
        await MouseClickAsync(point.Value.X, point.Value.Y, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ClickCodeNextAsync(CancellationToken cancellationToken)
    {
        var expression = """
            (() => {
              const blocked = ['다른 방법', 'try another', '다시 보내', 'resend', '건너뛰', 'skip'];
              const el = [...document.querySelectorAll('button')].find(node => {
                const text = (node.innerText || '').replace(/\s+/g, ' ').trim().toLowerCase();
                if (!text || blocked.some(item => text.includes(item))) return false;
                if (node.disabled || node.getAttribute('aria-disabled') === 'true') return false;
                return text === 'next' || text === '다음' || text === '확인';
              });
              if (!el) return '';
              el.scrollIntoView({block: 'center', inline: 'center'});
              const rect = el.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) return '';
              return JSON.stringify({x: rect.x + rect.width / 2, y: rect.y + rect.height / 2});
            })()
            """;
        var point = ParsePoint(await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false));
        if (point == null)
            return false;
        await MouseClickAsync(point.Value.X, point.Value.Y, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<string> ClickMatchingAccountAsync(string account, CancellationToken cancellationToken)
    {
        var wanted = JsonSerializer.Serialize(account.Trim().ToLowerInvariant());
        var expression = $$"""
            (() => {
              if (location.hostname !== 'accounts.google.com') return '';
              if (document.querySelector('input[type="password"]')) return '';
              const emailField = [...document.querySelectorAll('input[type="email"], input[name="identifier"], input#identifierId')].find(node => {
                const rect = node.getBoundingClientRect();
                return rect.width > 2 && rect.height > 2;
              });
              if (emailField) return '';
              const wanted = {{wanted}};
              const accounts = [...document.querySelectorAll('[data-identifier]')].filter(node => (node.getAttribute('data-identifier') || '').includes('@'));
              const match = accounts.find(node => (node.getAttribute('data-identifier') || '').toLowerCase() === wanted);
              const other = [...document.querySelectorAll('button, [role="button"], li, div')].find(node => {
                const text = (node.innerText || '').replace(/\s+/g, ' ').trim().toLowerCase();
                return text === 'use another account' || text === '다른 계정 사용';
              });
              const el = match || (accounts.length > 0 ? other : null);
              if (!el) return '';
              el.scrollIntoView({block: 'center', inline: 'center'});
              const rect = el.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) return '';
              return JSON.stringify({kind: match ? 'account' : 'other', x: rect.x + rect.width / 2, y: rect.y + rect.height / 2});
            })()
            """;
        var json = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return "";
        var click = JsonSerializer.Deserialize<AccountClick>(json, JsonOptions);
        if (click == null)
            return "";
        await MouseClickAsync(click.X, click.Y, cancellationToken).ConfigureAwait(false);
        return click.Kind == "account" ? "account" : "other";
    }

    private async Task<bool> ClickSignInAsync(CancellationToken cancellationToken)
    {
        var expression = """
            (() => {
              const nodes = [...document.querySelectorAll('button, [role="button"], a')];
              const el = nodes.find(node => {
                const text = (node.innerText || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const label = (node.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const hay = text + ' ' + label;
                if (hay.includes('로그아웃') || hay.includes('sign out')) return false;
                if (text === '로그인' || text === 'sign in' || label === '로그인' || label === 'sign in') return true;
                return hay.includes('google 계정으로 로그인') || hay.includes('sign in with google');
              });
              if (!el) return '';
              el.scrollIntoView({block: 'center', inline: 'center'});
              const rect = el.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) return '';
              return JSON.stringify({x: rect.x + rect.width / 2, y: rect.y + rect.height / 2});
            })()
            """;
        var point = ParsePoint(await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false));
        if (point == null)
            return false;
        await MouseClickAsync(point.Value.X, point.Value.Y, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ClickIndexAsync(int index, CancellationToken cancellationToken)
    {
        var expression = $$"""
            (() => {
              const nodes = [...document.querySelectorAll('{{Buttons}}')];
              const el = nodes[{{index}}];
              if (!el) return '';
              el.scrollIntoView({block: 'center', inline: 'center'});
              const rect = el.getBoundingClientRect();
              if (rect.width < 2 || rect.height < 2) return '';
              return JSON.stringify({x: rect.x + rect.width / 2, y: rect.y + rect.height / 2});
            })()
            """;
        var json = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        var point = ParsePoint(json);
        if (point == null)
            return false;
        await MouseClickAsync(point.Value.X, point.Value.Y, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task MouseClickAsync(double x, double y, CancellationToken cancellationToken)
    {
        await SendAsync(_page!, "Input.dispatchMouseEvent", new Dictionary<string, object?>
        {
            ["type"] = "mousePressed",
            ["x"] = x,
            ["y"] = y,
            ["button"] = "left",
            ["clickCount"] = 1,
        }, cancellationToken).ConfigureAwait(false);
        await SendAsync(_page!, "Input.dispatchMouseEvent", new Dictionary<string, object?>
        {
            ["type"] = "mouseReleased",
            ["x"] = x,
            ["y"] = y,
            ["button"] = "left",
            ["clickCount"] = 1,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await SendAsync(_page!, "Runtime.evaluate", new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
        }, cancellationToken).ConfigureAwait(false);
        if (result == null)
            return null;
        if (result.Value.TryGetProperty("exceptionDetails", out _))
            return null;
        if (!result.Value.TryGetProperty("result", out var inner))
            return null;
        if (!inner.TryGetProperty("value", out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static (double X, double Y)? ParsePoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var point = JsonSerializer.Deserialize<PointDto>(json, JsonOptions);
        return point == null ? null : (point.X, point.Y);
    }

    private async Task<JsonElement?> SendAsync(
        ClientWebSocket socket,
        string method,
        Dictionary<string, object?> args,
        CancellationToken cancellationToken,
        bool ignoreError = false)
    {
        var id = _nextId++;
        var payload = JsonSerializer.Serialize(new CdpRequest { Id = id, Method = method, Params = args });
        await socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var text = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("id", out var got) || got.GetInt32() != id)
                continue;
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (ignoreError)
                    return null;
                var message = error.TryGetProperty("message", out var textNode) ? textNode.GetString() : "브라우저 명령을 보내지 못했습니다.";
                throw new InvalidOperationException(message);
            }

            if (!document.RootElement.TryGetProperty("result", out var result))
                return null;
            return result.Clone();
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new IOException("브라우저 연결이 끊겼습니다.");
            stream.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<ClientWebSocket> OpenSocketAsync(string url, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        return socket;
    }

    private async Task CloseBrowserQuietlyAsync()
    {
        try
        {
            if (_browser is { State: WebSocketState.Open })
                await SendAsync(_browser, "Browser.close", new Dictionary<string, object?>(), CancellationToken.None, ignoreError: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or InvalidOperationException)
        {
        }

        try
        {
            if (_chrome is { HasExited: false })
                _chrome.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void DisposeSockets()
    {
        _page?.Dispose();
        _browser?.Dispose();
        _page = null;
        _browser = null;
        _chrome?.Dispose();
        _chrome = null;
    }

    private enum FillResult
    {
        Missing,
        Failed,
        Done,
    }

    private sealed class LoginProgress
    {
        public bool Raised;
        public bool VerificationRaised;
        public bool EmailSent;
        public bool PasswordSent;
        public int EmailAttempts;
        public int PasswordAttempts;
        public int SignInClicks;
        public int AccountClicks;
        public int ConfirmClicks;
        public int CodeAdvances;
        public int PasskeySkips;
        public DateTime NextAction;
        public DateTime NextSignIn;
        public DateTime NextAccount;
        public DateTime NextCode;
        public DateTime NextPasskey;
    }

    private sealed class AccountClick
    {
        public string Kind { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class Snapshot
    {
        public string Href { get; set; } = "";
        public string Body { get; set; } = "";
        public List<MeetControl> Controls { get; set; } = [];
    }

    private sealed class PointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class CdpRequest
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public Dictionary<string, object?> Params { get; set; } = new();
    }
}
