using System;
using System.Collections.Generic;

public static class UpdateCheckTest
{
    static readonly Version Installed = new Version(1, 0, 0);
    const string Uploaded = "{\"name\":\"DeskAudio.exe\",\"state\":\"uploaded\",\"size\":1234}";
    static int passed;

    static string Json(string tag, string assets = Uploaded, string flags = "\"draft\":false,\"prerelease\":false")
    {
        return "{\"tag_name\":\"" + tag + "\"," + flags + ",\"assets\":[" + assets + "]}";
    }
    static void Expect(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        passed++;
    }
    static UpdateResult Parse(string json)
    {
        return UpdateCheck.CheckCore(Installed, (list, token) => new UpdateCheck.Reply(200, json),
            () => { throw new Exception("Local credentials were requested for a public release."); });
    }
    static void Access(string label, string credential, int[] codes, UpdateState state, string[] wanted, bool limited = false)
    {
        var calls = new List<string>();
        int i = 0;
        UpdateResult result = UpdateCheck.CheckCore(Installed, delegate(bool list, string token) {
            calls.Add((list ? "list" : "latest") + (token == null ? "-" : "+"));
            return new UpdateCheck.Reply(codes[i++], Json("v1.1.0"), limited);
        }, () => credential);
        Expect(result.State == state && String.Join(" ", wanted) == String.Join(" ", calls), label);
    }
    public static int Main()
    {
        Func<string> denied = () => { throw new Exception("An unauthorized credential store was read."); };
        Expect(UpdateCheck.ChooseCredential(null, () => null, () => false, denied) == null, "machine account is opt-in");
        Expect(UpdateCheck.ChooseCredential(null, () => "saved", () => false, denied) == "saved", "application credential remains independent");
        Expect(UpdateCheck.ChooseCredential(" env ", denied, () => false, denied) == "env", "explicit process credential takes priority");
        Expect(UpdateCheck.ChooseCredential(null, () => null, () => true, () => "account") == "account", "authorized machine account");
        Expect(Parse(Json("v1.0.0")).State == UpdateState.Current, "equal stable version");
        Expect(Parse(Json("v0.99.0")).State == UpdateState.Current, "installed version ahead");
        var available = Parse(Json("v1.10.0"));
        Expect(available.State == UpdateState.Available && available.Tag == "v1.10.0", "numeric version comparison");
        Expect(available.PageUrl.EndsWith("/releases/tag/v1.10.0") && available.AssetUrl == UpdateCheck.RepositoryUrl + "/releases/download/v1.10.0/DeskAudio.exe", "canonical asset");
        Expect(Parse(Json("v1.0.0+build.3")).State == UpdateState.Current, "build metadata");
        Expect(Parse(Json("v1.0.0-beta.1")).State == UpdateState.Failed, "prerelease label");
        Expect(Parse(Json("v1.0.0/../../else")).State == UpdateState.Failed, "malformed tag");
        Expect(Parse(Json("v1.0.0", Uploaded, "\"prerelease\":true,\"draft\":false")).State == UpdateState.Failed, "prerelease flag");
        Expect(Parse(Json("v1.0.0", Uploaded, "\"prerelease\":false,\"draft\":true")).State == UpdateState.Failed, "draft flag");
        Expect(Parse(Json("v1.0.0", "")).State == UpdateState.Failed, "missing executable");
        Expect(Parse(Json("v1.0.0", Uploaded.Replace("1234", "0"))).State == UpdateState.Failed, "empty executable");
        Expect(Parse(Json("v1.0.0", Uploaded.Replace("uploaded", "pending"))).State == UpdateState.Failed, "pending asset");
        Expect(Parse("[[").State == UpdateState.Failed, "bad JSON");
        Access("private success", "dummy", new[]{404, 200}, UpdateState.Available, new[]{"latest-","latest+"});
        Access("restricted without credential", null, new[]{404, 404}, UpdateState.NeedsAccess, new[]{"latest-","list-"});
        Access("public without release", null, new[]{404, 200}, UpdateState.NoRelease, new[]{"latest-","list-"});
        Access("private without release", "dummy", new[]{404, 404, 200}, UpdateState.NoRelease, new[]{"latest-","latest+","list+"});
        Access("inaccessible private repo", "dummy", new[]{404, 404, 404}, UpdateState.NeedsAccess, new[]{"latest-","latest+","list+"});
        Access("rejected credential", "dummy", new[]{404, 401}, UpdateState.NeedsAccess, new[]{"latest-","latest+"});
        Access("missing permissions", "dummy", new[]{404, 403}, UpdateState.NeedsAccess, new[]{"latest-","latest+"});
        Access("rate-limited", null, new[]{429}, UpdateState.Failed, new[]{"latest-"}, true);
        Access("refuse redirects", "dummy", new[]{302}, UpdateState.Failed, new[]{"latest-"});
        Access("invalid token", "bad\r\nheader", new[]{404}, UpdateState.NeedsAccess, new[]{"latest-"});
        Access("server error", "dummy", new[]{500}, UpdateState.Failed, new[]{"latest-"});
        Console.WriteLine(passed + " offline update checks passed.");
        return 0;
    }
}
