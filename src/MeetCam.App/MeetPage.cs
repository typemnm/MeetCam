using System.Text.Json;

namespace MeetCam;

internal enum MeetStage
{
    Unknown,
    Login,
    Lobby,
    Waiting,
    Denied,
    Invalid,
    InCall,
}

internal readonly record struct MeetControl(int Index, string Text, string Label, string Muted);

internal static class MeetPage
{
    private static readonly string[] JoinPhrases =
    [
        "join now",
        "ask to join",
        "join anyway",
        "지금 참여",
        "참여하기",
        "참가 요청",
        "참여 요청",
    ];

    private static readonly string[] LeavePhrases =
    [
        "leave call",
        "통화에서 나가기",
        "회의에서 나가기",
        "통화 종료",
    ];

    private static readonly string[] WaitingPhrases =
    [
        "asking to join",
        "asking to be let in",
        "lets you in",
        "let you in",
        "you'll join",
        "참여를 요청",
        "참가 요청 중",
        "참여 요청 중",
        "허용할 때까지",
        "승인할 때까지",
        "승인해 줄 때까지",
        "주최자가 참여",
        "관리자가",
        "기다려 주세요",
        "기다려주세요",
    ];

    public static MeetStage Classify(string? href, string? body, IReadOnlyList<MeetControl> controls)
    {
        var lowerBody = (body ?? "").ToLowerInvariant();
        if (IsAccountsPage(href))
            return MeetStage.Login;
        if (FindLeave(controls) >= 0)
            return MeetStage.InCall;
        if (NeedsAccount(lowerBody) || FindSignIn(controls) >= 0)
            return MeetStage.Login;
        if (IsInvalid(lowerBody))
            return MeetStage.Invalid;
        if (IsDenied(lowerBody))
            return MeetStage.Denied;
        if (FindJoin(controls) >= 0)
            return MeetStage.Lobby;
        if (IsWaiting(lowerBody))
            return MeetStage.Waiting;
        return MeetStage.Unknown;
    }

    public static bool IsAccountsPage(string? href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return false;
        if (uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("consent.google.com", StringComparison.OrdinalIgnoreCase))
            return true;
        return uri.Host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/sorry", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsThisMeeting(string? href, string meetingUrl)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var page))
            return false;
        if (!page.Host.Equals("meet.google.com", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Uri.TryCreate(meetingUrl, UriKind.Absolute, out var meeting))
            return false;
        var path = meeting.AbsolutePath.Trim('/');
        return path.Length > 0 && page.AbsolutePath.Contains(path, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPasskeyOffer(string? href, string? body)
    {
        if (!IsAccountsPage(href))
            return false;
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.Contains("passkeyenrollment", StringComparison.OrdinalIgnoreCase))
            return true;
        var lower = (body ?? "").ToLowerInvariant();
        return lower.Contains("패스키를 사용해 빠르게 로그인", StringComparison.Ordinal)
            || lower.Contains("sign in faster with a passkey", StringComparison.Ordinal)
            || lower.Contains("sign in faster with passkeys", StringComparison.Ordinal);
    }

    public static bool IsPasskeySkip(string? text) => Matches(PasskeySkipPhrases, text);

    public static string PasskeySkipList() => QuoteAll(PasskeySkipPhrases);

    public static bool IsPasswordPage(string? href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return false;
        var path = uri.AbsolutePath;
        return path.Contains("/pwd", StringComparison.OrdinalIgnoreCase)
            || path.Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsConfirmPage(string? href)
    {
        if (!IsAccountsPage(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return false;
        return uri.AbsolutePath.Contains("confirmidentifier", StringComparison.OrdinalIgnoreCase);
    }

    // Password entry is not the phone check. /sorry and other challenge pages are.
    public static bool IsVerification(string? href, string? body)
    {
        if (!IsAccountsPage(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return false;
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.Contains("/pwd", StringComparison.Ordinal) || path.Contains("password", StringComparison.Ordinal))
            return false;
        if (path.Contains("/identifier", StringComparison.Ordinal) || path.Contains("accountchooser", StringComparison.Ordinal))
            return false;
        if (path.StartsWith("/sorry", StringComparison.Ordinal)
            || path.Contains("/challenge/", StringComparison.Ordinal)
            || path.Contains("signin/challenge", StringComparison.Ordinal))
            return true;
        var lower = (body ?? "").ToLowerInvariant();
        return lower.Contains("2단계 인증", StringComparison.Ordinal)
            || lower.Contains("2-step verification", StringComparison.Ordinal)
            || lower.Contains("verify it's you", StringComparison.Ordinal)
            || lower.Contains("본인 확인", StringComparison.Ordinal)
            || lower.Contains("휴대전화", StringComparison.Ordinal)
            || lower.Contains("휴대폰", StringComparison.Ordinal)
            || lower.Contains("휴대기기", StringComparison.Ordinal)
            || lower.Contains("check your phone", StringComparison.Ordinal)
            || lower.Contains("check your device", StringComparison.Ordinal)
            || lower.Contains("인증 코드", StringComparison.Ordinal)
            || lower.Contains("enter the code", StringComparison.Ordinal);
    }

    public static bool IsPasswordRejected(string? body)
    {
        var lower = (body ?? "").ToLowerInvariant();
        return lower.Contains("wrong password", StringComparison.Ordinal)
            || lower.Contains("비밀번호가 잘못", StringComparison.Ordinal)
            || lower.Contains("잘못된 비밀번호", StringComparison.Ordinal);
    }

    public static bool IsBrowserBlocked(string? body)
    {
        var lower = (body ?? "").ToLowerInvariant();
        return lower.Contains("this browser or app may not be secure", StringComparison.Ordinal)
            || lower.Contains("couldn't sign you in", StringComparison.Ordinal)
            || lower.Contains("couldn’t sign you in", StringComparison.Ordinal)
            || lower.Contains("안전하지 않을 수 있습니다", StringComparison.Ordinal);
    }

    public static int FindSignIn(IReadOnlyList<MeetControl> controls)
    {
        for (var i = 0; i < controls.Count; i++)
        {
            if (IsSignIn(controls[i]))
                return controls[i].Index;
        }
        return -1;
    }

    public static bool IsStayButton(string? text) => Matches(StayPhrases, text);

    public static bool IsAckButton(string? text) => Matches(AckPhrases, text);

    public static string StayPhraseList() => QuoteAll(StayPhrases);

    public static string AckPhraseList() => QuoteAll(AckPhrases);

    public static int FindJoin(IReadOnlyList<MeetControl> controls)
    {
        foreach (var phrase in JoinPhrases)
        {
            var index = FindPhrase(controls, phrase);
            if (index >= 0)
                return index;
        }
        return -1;
    }

    public static int FindLeave(IReadOnlyList<MeetControl> controls)
    {
        foreach (var phrase in LeavePhrases)
        {
            var index = FindPhrase(controls, phrase);
            if (index >= 0)
                return index;
        }
        return -1;
    }

    public static int FindMicrophone(IReadOnlyList<MeetControl> controls)
    {
        var fallback = -1;
        for (var i = 0; i < controls.Count; i++)
        {
            if (!IsMicrophone(controls[i]) || IsCamera(controls[i]))
                continue;
            var hay = Haystack(controls[i]);
            if (hay.Contains("option", StringComparison.Ordinal) || hay.Contains("옵션", StringComparison.Ordinal) || hay.Contains("more", StringComparison.Ordinal))
                continue;
            if (controls[i].Muted.Length > 0)
                return controls[i].Index;
            if (fallback < 0)
                fallback = controls[i].Index;
        }
        return fallback;
    }

    public static int FindCamera(IReadOnlyList<MeetControl> controls)
    {
        var fallback = -1;
        for (var i = 0; i < controls.Count; i++)
        {
            if (!IsCamera(controls[i]) || IsMicrophone(controls[i]))
                continue;
            var hay = Haystack(controls[i]);
            if (hay.Contains("option", StringComparison.Ordinal) || hay.Contains("옵션", StringComparison.Ordinal) || hay.Contains("more", StringComparison.Ordinal))
                continue;
            if (controls[i].Muted.Length > 0)
                return controls[i].Index;
            if (fallback < 0)
                fallback = controls[i].Index;
        }
        return fallback;
    }

    public static int FindCameraOptions(IReadOnlyList<MeetControl> controls)
    {
        for (var i = 0; i < controls.Count; i++)
        {
            var hay = Haystack(controls[i]);
            var camera = hay.Contains("camera", StringComparison.Ordinal) || hay.Contains("카메라", StringComparison.Ordinal);
            var options = hay.Contains("option", StringComparison.Ordinal) || hay.Contains("옵션", StringComparison.Ordinal) || hay.Contains("more", StringComparison.Ordinal);
            if (camera && options)
                return i;
        }
        return -1;
    }

    // data-is-muted="true" means the camera is off. The label is the action, so "끄기" means it is currently on.
    public static bool? CameraIsOff(MeetControl control)
    {
        if (control.Muted.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (control.Muted.Equals("false", StringComparison.OrdinalIgnoreCase))
            return false;
        var hay = Haystack(control);
        if (hay.Contains("turn off", StringComparison.Ordinal) || hay.Contains("끄기", StringComparison.Ordinal) || hay.Contains("사용 중지", StringComparison.Ordinal))
            return false;
        if (hay.Contains("turn on", StringComparison.Ordinal) || hay.Contains("켜기", StringComparison.Ordinal))
            return true;
        return null;
    }

    public static void SelfCheck()
    {
        var join = new MeetControl(0, "참가 요청", "", "");
        var lobby = Classify("https://meet.google.com/abc-defg-hij", "관리자가 승인할 때까지", [join]);
        if (lobby != MeetStage.Lobby)
            throw new InvalidOperationException("join button is still the lobby");

        var waiting = Classify("https://meet.google.com/abc-defg-hij", "관리자가 승인할 때까지 기다려 주세요", []);
        if (waiting != MeetStage.Waiting)
            throw new InvalidOperationException("approval wait was not detected");

        var inside = Classify(
            "https://meet.google.com/abc-defg-hij",
            "관리자가 승인했습니다",
            [new MeetControl(1, "", "통화에서 나가기", "")]);
        if (inside != MeetStage.InCall)
            throw new InvalidOperationException("leave button should mean the call has started");

        var login = Classify("https://accounts.google.com/signin", "", []);
        if (login != MeetStage.Login)
            throw new InvalidOperationException("login page was not detected");

        var guestControls = new[]
        {
            new MeetControl(0, "참여 요청", "", ""),
            new MeetControl(4, "로그인", "", ""),
        };
        var guest = Classify("https://meet.google.com/abc-defg-hij", "이름이 무엇인가요?", guestControls);
        if (guest != MeetStage.Login || FindSignIn(guestControls) != 4)
            throw new InvalidOperationException("guest name page should open Google sign-in");
        var signedIn = Classify("https://meet.google.com/abc-defg-hij", "카메라", [new MeetControl(0, "참여 요청", "", "")]);
        if (signedIn != MeetStage.Lobby)
            throw new InvalidOperationException("signed-in lobby was treated as login");
        if (!IsVerification("https://accounts.google.com/v3/signin/challenge/dp", ""))
            throw new InvalidOperationException("phone check was not detected");
        if (IsVerification("https://accounts.google.com/v3/signin/challenge/pwd", "휴대전화"))
            throw new InvalidOperationException("password page was treated as a phone check");
        if (!IsPasswordRejected("비밀번호가 잘못되었습니다") || IsPasswordRejected("참여를 요청했습니다"))
            throw new InvalidOperationException("password rejection was misread");
        if (!IsThisMeeting("https://meet.google.com/meet/abc-defg-hij", "https://meet.google.com/abc-defg-hij"))
            throw new InvalidOperationException("meeting redirect was not recognized");
        if (IsThisMeeting("https://workspace.google.com/products/meet/", "https://meet.google.com/abc-defg-hij"))
            throw new InvalidOperationException("marketing page was treated as the meeting");
        if (IsStayButton("나가기") || IsStayButton("홈 화면으로 돌아가기") || !IsStayButton("계속 대기") || !IsStayButton("다시 참여하기"))
            throw new InvalidOperationException("idle dialog buttons were misread");
        if (!IsPasskeyOffer("https://accounts.google.com/v3/signin/speedbump/passkeyenrollment", "")
            || IsPasskeyOffer("https://accounts.google.com/v3/signin/challenge/dp", "휴대폰으로 확인"))
            throw new InvalidOperationException("passkey offer was misread");
        if (!IsPasskeySkip("나중에") || IsPasskeySkip("계속") || IsPasskeySkip("패스키 만들기"))
            throw new InvalidOperationException("passkey skip button was misread");

        var denied = Classify("https://meet.google.com/abc-defg-hij", "참여 요청이 거부되었습니다", []);
        if (denied != MeetStage.Denied)
            throw new InvalidOperationException("denied page was not detected");

        var camera = new MeetControl(2, "", "카메라 켜기", "true");
        if (CameraIsOff(camera) != true)
            throw new InvalidOperationException("muted camera should stay off until approval");
        var devices = new[]
        {
            new MeetControl(6, "", "카메라 끄기", "false"),
            new MeetControl(5, "", "마이크 끄기", "false"),
            new MeetControl(7, "", "마이크 옵션", ""),
        };
        if (FindMicrophone(devices) != 5 || FindCamera(devices) != 6 || CameraIsOff(devices[1]) != false)
            throw new InvalidOperationException("hot microphone should be muted on entry");

        if (!MeetingPlan.TryNormalize("abc-defg-hij", out var fromCode) || fromCode != "https://meet.google.com/abc-defg-hij")
            throw new InvalidOperationException("meeting code was not accepted");
        if (MeetingPlan.TryNormalize("https://example.com/abc-defg-hij", out _))
            throw new InvalidOperationException("non-meet link was accepted");

        var parsed = JsonSerializer.Deserialize<MeetControl>(
            """{"index":3,"text":"참가 요청","label":"","muted":"true"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (parsed.Index != 3 || parsed.Text != "참가 요청" || parsed.Muted != "true")
            throw new InvalidOperationException("meeting controls did not parse");
    }

    private static readonly string[] PasskeySkipPhrases = ["나중에", "나중에 하기", "지금 안 함", "Not now", "Skip", "No thanks"];

    private static readonly string[] StayPhrases = ["계속 대기", "다시 참여하기", "Keep waiting", "I'm still here", "Rejoin"];

    private static readonly string[] AckPhrases = ["Got it", "확인", "알겠습니다", "닫기"];

    private static bool NeedsAccount(string lowerBody)
    {
        return lowerBody.Contains("use your google account", StringComparison.Ordinal)
            || lowerBody.Contains("google 계정으로 로그인", StringComparison.Ordinal)
            || lowerBody.Contains("이름이 무엇인가요", StringComparison.Ordinal)
            || lowerBody.Contains("what's your name", StringComparison.Ordinal)
            || lowerBody.Contains("what is your name", StringComparison.Ordinal);
    }

    private static bool IsSignIn(MeetControl control)
    {
        var hay = Haystack(control);
        if (hay.Contains("로그아웃", StringComparison.Ordinal) || hay.Contains("sign out", StringComparison.Ordinal))
            return false;
        if (Same(control.Text, "로그인") || Same(control.Text, "sign in")
            || Same(control.Label, "로그인") || Same(control.Label, "sign in"))
            return true;
        return hay.Contains("google 계정으로 로그인", StringComparison.Ordinal)
            || hay.Contains("sign in with google", StringComparison.Ordinal);
    }

    private static bool Same(string value, string phrase)
        => value.Trim().Equals(phrase, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string[] phrases, string? text)
    {
        var trimmed = (text ?? "").Trim();
        foreach (var phrase in phrases)
        {
            if (trimmed.Equals(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string QuoteAll(string[] phrases)
        => string.Join(", ", phrases.Select(static phrase => JsonSerializer.Serialize(phrase)));

    private static bool IsWaiting(string lowerBody)
    {
        foreach (var phrase in WaitingPhrases)
        {
            if (lowerBody.Contains(phrase, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsDenied(string lowerBody)
    {
        return lowerBody.Contains("denied your request", StringComparison.Ordinal)
            || lowerBody.Contains("참여 요청이 거부", StringComparison.Ordinal)
            || lowerBody.Contains("참가 요청이 거부", StringComparison.Ordinal)
            || lowerBody.Contains("참여가 거부", StringComparison.Ordinal)
            || lowerBody.Contains("참여할 수 없습니다", StringComparison.Ordinal);
    }

    private static bool IsInvalid(string lowerBody)
    {
        return lowerBody.Contains("check your meeting code", StringComparison.Ordinal)
            || lowerBody.Contains("잘못된 회의", StringComparison.Ordinal);
    }

    private static int FindPhrase(IReadOnlyList<MeetControl> controls, string phrase)
    {
        for (var i = 0; i < controls.Count; i++)
        {
            if (Haystack(controls[i]).Contains(phrase, StringComparison.Ordinal))
                return controls[i].Index;
        }
        return -1;
    }

    private static string Haystack(MeetControl control)
        => (control.Text + " " + control.Label).ToLowerInvariant();

    private static bool IsCamera(MeetControl control)
    {
        var hay = Haystack(control);
        return hay.Contains("camera", StringComparison.Ordinal) || hay.Contains("카메라", StringComparison.Ordinal);
    }

    private static bool IsMicrophone(MeetControl control)
    {
        var hay = Haystack(control);
        return hay.Contains("microphone", StringComparison.Ordinal)
            || hay.Contains("mic", StringComparison.Ordinal)
            || hay.Contains("마이크", StringComparison.Ordinal);
    }
}
