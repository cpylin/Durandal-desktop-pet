// PetNotify.cs —— Claude Code hook 的转发器（热路径）
//
// 这个程序每次工具调用都会被触发，所以刻意做得极简：
//   * 不引用 WPF（避免加载几百个程序集，启动要快）
//   * 不依赖任何 JSON 库，自带一个极小的字段提取器
//   * 只做一件事：读 stdin -> 发一个 UDP 数据报 -> 退出
//   * 全程 try/catch 吞掉所有异常，永远返回 0
//
// 桌宠没运行、端口被占、JSON 畸形……都不会影响 Claude Code 本身。
// 这是硬要求：hook 默认是同步的，慢或报错会拖住整个会话。
//
// 用法：
//   PetNotify.exe <状态>        # 由 hooks.json 显式传入，推荐
//   PetNotify.exe               # 不给状态时，从 stdin 的 hook_event_name 推断
//
// 状态取值：awake think work alert done sleep error idle

using System;
using System.Net.Sockets;
using System.Text;

class PetNotify
{
    // 必须和 Pet.cs 的 DefaultPort 一致 —— 这两个是**独立编译的 exe**，改一边忘另一边就失联，
    // 而且症状是"桌宠完全没反应"，很难往端口上想。实际端口以 Pet 读到的 config.json 为准。
    const int DefaultPort = 47821;

    static int Main(string[] argv)
    {
        EmitAsyncHandshake();
        try
        {
            string stdin = ReadStdin();

            string state = (argv.Length > 0) ? argv[0] : "";
            string ev = Extract(stdin, "hook_event_name");
            if (state.Length == 0) state = MapEvent(ev);
            if (state.Length == 0) state = "idle";

            string tool = Extract(stdin, "tool_name");
            string sid  = Extract(stdin, "session_id");

            // 会话开始 / 结束时带上项目名（hook 载荷里有 cwd），桌宠会把这句话显示在气泡里，
            // 顺便把上一次残留的姿势（一直举着手、或者已经睡了）刷新掉。
            // 只有这两个事件需要，其他事件不走这段，热路径不受影响。
            string note = "";
            if (ev == "SessionStart" || ev == "SessionEnd")
            {
                note = (ev == "SessionStart") ? "会话开始" : "会话结束";
                string proj = ProjectName(Extract(stdin, "cwd"));
                if (proj.Length > 0) note = note + " · " + proj;
            }

            // 载荷用竖线分隔，两端都不需要 JSON 解析器。
            // 工具名 / session_id / 备注里都不会出现竖线，无需转义。
            string payload = state + "|" + tool + "|" + sid + "|" + note;
            byte[] bytes = Encoding.UTF8.GetBytes(payload);

            UdpClient udp = new UdpClient();
            try
            {
                udp.Send(bytes, bytes.Length, "127.0.0.1", ResolvePort());
            }
            finally
            {
                udp.Close();
            }
        }
        catch (Exception)
        {
            // 有意吞掉：桌宠是装饰品，绝不能影响主流程
        }
        return 0;
    }

    // 异步握手：Claude Code 看到 stdout 第一行的 {"async":true} 就不再等这个
    // hook 跑完，会话不会被拖慢。这是官方插件（security-guidance）在用的机制。
    //
    // 万一你的版本上行为不对（比如报 JSON 解析错误），设环境变量
    // CLAUDE_PET_SYNC=1 即可退回同步模式。
    static void EmitAsyncHandshake()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("CLAUDE_PET_SYNC") == "1") return;
            Console.Out.Write("{\"async\": true, \"asyncTimeout\": 10000}\n");
            Console.Out.Flush();
        }
        catch (Exception)
        {
            // 没有 stdout 也无所谓，继续走同步路径即可
        }
    }

    static int ResolvePort()
    {
        try
        {
            string env = Environment.GetEnvironmentVariable("CLAUDE_PET_PORT");
            int p;
            if (env != null && int.TryParse(env, out p) && p > 0 && p < 65536) return p;
        }
        catch (Exception) { }
        return DefaultPort;
    }

    static string ReadStdin()
    {
        try
        {
            // 没有重定向输入时千万不要读，否则会挂住等键盘
            if (Console.IsInputRedirected) return Console.In.ReadToEnd();
        }
        catch (Exception) { }
        return "";
    }

    static string MapEvent(string ev)
    {
        switch (ev)
        {
            case "SessionStart":       return "awake";
            case "UserPromptSubmit":   return "think";
            case "PreToolUse":         return "work";
            case "PostToolUse":        return "think";   // 工具跑完，回到思考
            case "PostToolUseFailure": return "error";
            case "SubagentStart":      return "work";
            case "SubagentStop":       return "think";
            case "PreCompact":         return "think";
            case "Notification":       return "alert";
            case "Stop":               return "done";
            case "SessionEnd":         return "sleep";
        }
        return "";
    }

    // C:\Users\user\ClaudePet -> ClaudePet（末尾带斜杠也能处理；认不出就返回空串）
    static string ProjectName(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        string s = cwd.TrimEnd('\\', '/');
        int i = s.LastIndexOfAny(new char[] { '\\', '/' });
        if (i >= 0) s = s.Substring(i + 1);
        return s;
    }

    // ---- 极简 JSON 字符串字段提取 ----
    // 只找顶层第一个 "key": "value"，够用即可。不处理嵌套作用域，
    // 对 hook 载荷来说不存在歧义。
    static string Extract(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "";

        string pat = "\"" + key + "\"";
        int i = json.IndexOf(pat, StringComparison.Ordinal);
        if (i < 0) return "";

        i = json.IndexOf(':', i + pat.Length);
        if (i < 0) return "";

        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return "";
        i++;

        StringBuilder sb = new StringBuilder();
        while (i < json.Length && json[i] != '"')
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                i++;
                char e = json[i];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'r') sb.Append('\r');
                else if (e == 'u' && i + 4 < json.Length) i += 4;   // 跳过 \uXXXX
                else sb.Append(e);
            }
            else
            {
                sb.Append(c);
            }
            i++;
        }
        return sb.ToString();
    }
}
