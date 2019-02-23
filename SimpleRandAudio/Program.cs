using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Un4seen.Bass;

namespace SimpleRandAudio {
    internal class Program {
        private static readonly string[] exts = { ".mp3", ".flac", ".oog", ".wav" };

        private static bool Check(string file) {
            if (file is null) return false;
            if (file.Length <= 0) return false;
            file = file.ToLower();
            foreach (var ext in exts)
                if (file.EndsWith(ext)) return true;
            return false;
        }

        private static string Sec2Str(double sec) {
            if (sec < 3600) {
                var mi = (int)(sec / 60);
                sec -= mi * 60;
                var mi_str = mi < 10 ? $"0{mi.ToString()}" : mi.ToString();
                var sec_str = sec < 10 ? $"0{sec.ToString("F1")}" : sec.ToString("F1");
                return $"{mi_str}:{sec_str}";
            } else {
                var hr = (int)(sec / 3600);
                sec -= hr * 3600;
                return $"{hr.ToString()}:{Sec2Str(sec)}";
            }
        }

        private static Random RCutN(Random r, int n) {
            while (--n >= 0)
                r.NextDouble();
            return r;
        }
        private static void Main(string[] args) {
            Console.OutputEncoding = Encoding.Unicode;
            Console.InputEncoding = Encoding.Unicode;
            Thread.Sleep(1);
            var rand = RCutN(new Random((int)(DateTime.UtcNow.Ticks % 0x7FFFFFFF)), Environment.TickCount % 257);
            var mainmoddir = Path.GetDirectoryName(Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName));
            if (Directory.Exists(mainmoddir))
                Bass.BASS_PluginLoadDirectory(mainmoddir);
            if (!Bass.BASS_Init(-1, 96000, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero)) {
                Console.Write($"Init failed:{Bass.BASS_ErrorGetCode().ToString()} ...");
                Console.ReadKey(true);
                return;
            }
            string GetFullPath(string path) {
                try {
                    return Path.GetFullPath(path);
                } catch {
                    return null;
                }
            }
            var narg = args.Length;
            var iarg = 0;
            //Bass.BASS_SetVolume(0.5f);
            Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_GVOL_STREAM, 2500);
            var osv = Environment.OSVersion;
            if (osv.Platform != PlatformID.Win32NT || osv.Version.Major < 10)
                Console.SetBufferSize(256, 9999);
            string cmd;
            List<string> files = null;
            var datas = new HashSet<string>();
            string listfile = null;
            for (; ; ) {
                Console.Write("输入文件夹/列表文件>");
                if (iarg < narg)
                    Console.WriteLine(cmd = args[iarg++]);
                else
                    cmd = Console.ReadLine();
                cmd = cmd.Replace("\"", string.Empty);
                if (!(Directory.Exists(cmd) || File.Exists(cmd))) {
                    Console.WriteLine($"路径 {cmd} 不存在/不合法");
                    continue;
                }
                cmd = GetFullPath(cmd);
                if (File.Exists(cmd)) {
                    foreach (var _file in File.ReadAllLines(cmd, Encoding.UTF8)) {
                        if (_file is null) continue;
                        if (_file.Length <= 0) continue;
                        var file = _file;
                        if (int.TryParse(file, out var number)) {
                            Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_GVOL_STREAM, Math.Min(10000, Math.Max(0, number)));
                        } else if (Check(file = GetFullPath(file.Replace("\"", string.Empty))) && datas.Add(file))
                            Console.WriteLine(file);
                    }
                    listfile = cmd;
                } else {
                    var last_time = DateTime.Now.AddSeconds(-1);
                    void f(string path) {
                        var t = DateTime.Now;
                        if (t.Subtract(last_time).TotalMilliseconds > 33) {
                            last_time = t;
                            Console.Title = $"...{path}";
                        }
                        try {
                            foreach (var file in Directory.GetFiles(path))
                                if (Check(file))
                                    if (datas.Add(file))
                                        Console.WriteLine(file);
                        } catch (Exception e) {
                            Console.WriteLine(e);
                        }
                        try {
                            foreach (var dir in Directory.GetDirectories(path))
                                f(dir);
                        } catch (Exception e) {
                            Console.WriteLine(e);
                        }
                    }
                    f(cmd);
                }
                Console.WriteLine($"一共{datas.Count}个文件");
                files = new List<string>(datas);
                break;
            }
            var nfile = files.Count;
            cmd = null;
            var ch = new Mutex();
            (new Thread(() => {
                for (; ; ) {
                    if (cmd is null) {
                        ch.WaitOne();
                        Console.Write('>'); cmd = Console.ReadLine();
                        ch.ReleaseMutex();
                        if (cmd is "exit" || cmd is "quit")
                            break;
                    }
                    Thread.Sleep(666);
                }
            })).Start();
            var started = false;
            var notquit = true;
            var re_vol = new Regex(@"^vol\s+(?<number>\d+)\s?$", RegexOptions.Compiled | RegexOptions.ExplicitCapture);
            void save() {
                try {
                    File.WriteAllLines(listfile, files, Encoding.UTF8);
                    File.AppendAllText(listfile, $"\n{Bass.BASS_GetConfig(BASSConfig.BASS_CONFIG_GVOL_STREAM).ToString()}\n");
                    Console.WriteLine($"已保存到{listfile}");
                } catch (Exception e) {
                    Console.WriteLine($"保存到{listfile}失败");
                    Console.WriteLine(e);
                }
            }
            string playing = null;
            double last_pos;
            Predicate<string> toplay = null;
            bool pcmd() {
                if (cmd is null) return true;
                ch.WaitOne();
                switch (cmd) {
                    case "exit":
                    case "quit": {
                        return notquit = false;
                    }
                    case "start": started = true; break;
                    case "":
                    case "help":
                    case "?":
                        Console.WriteLine(@"
命令列表:
    help/?          显示当前信息
    start           开始播放列表
    stop            停止播放(不会记录播放位置)
    next            下一首
    now/playing/`/??输出当前正在播放的文件路径
    exit/quit       退出
    save <path>     保存当前播放列表到文件<path>，包括音量
    vol [<value>]   设置/显示当前音量，值范围 [0, 10000] 整数，如果已有列表文件(启动加载时输入的或者中途save命令保存的)，会同时保存到列表文件里
    ?<pattern>      使用 pattern 筛选显示歌曲路径，pattern 以 \ 开头则为正则表达式(除了开头的\外)，否则为子串查找
    =<pattern>      播放符合 pattern 的第一首歌曲，pattern 格式同 ?<pattern>
    -<pattern>      从列表里删除所有符合 pattern 的歌曲，pattern 格式同 ?<pattern>，如果已有列表文件(启动加载时输入的或者中途save命令保存的)，会同时保存到列表文件里
    add <path>      增加 path 文件夹下/文件的歌曲，如果已有列表文件(启动加载时输入的或者中途save命令保存的)，会同时保存到列表文件里
    =               (不含 pattern) 还没开始播放时相当于 start 命令;开始播放后相当于 next 命令
    -               (不含 pattern) 还没开始播放时没有效果;开始播放后相当于 stop 命令
");
                        break;
                    case "next": {
                        cmd = null;
                        ch.ReleaseMutex();
                        return false;
                    }
                    case "??":
                    case "now":
                    case "`":
                    case "playing":
                        Console.WriteLine(playing ?? "<当前没有播放任何文件>");
                        break;
                    case "stop": {
                        cmd = null;
                        ch.ReleaseMutex();
                        started = false;
                        if (!(playing is null))
                            Console.WriteLine($"之前播放: {playing}");
                        return false;
                    }
                    case "=": {
                        if (started) {
                            goto case "next";
                        } else {
                            goto case "start";
                        }
                    }
                    case "-":
                        if (started) {
                            goto case "stop";
                        } else {
                            Console.WriteLine("当前没有播放");
                        }
                        break;
                    default:
                        if (cmd.StartsWith("save")) {
                            if (cmd.Length > 5)
                                listfile = GetFullPath(cmd.Substring(5).Replace("\"", string.Empty));
                            if (listfile is null || listfile is "") {
                                Console.WriteLine("没有导出路径，请使用 save <file path> 命令指定导出路径");
                            } else {
                                save();
                            }
                        } else if (cmd.StartsWith("vol")) {
                            var m = re_vol.Match(cmd);
                            if (m.Success) {
                                if (Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_GVOL_STREAM, Math.Min(10000, Math.Max(0, int.Parse(m.Groups["number"].Value))))) {
                                    Console.WriteLine("音量设置成功");
                                    if (!(listfile is null) && File.Exists(listfile))
                                        save();
                                } else {
                                    Console.WriteLine($"设置失败:{Bass.BASS_ErrorGetCode().ToString()}");
                                }
                            } else {
                                Console.WriteLine($"当前音量:{Bass.BASS_GetConfig(BASSConfig.BASS_CONFIG_GVOL_STREAM)}");
                            }
                        } else if (cmd.StartsWith("add")) {
                            var path = GetFullPath(cmd.Substring(4).Replace("\"", string.Empty));
                            if (File.Exists(path)) {
                                if (Check(path))
                                    if (datas.Add(path)) {
                                        files.Add(path);
                                        nfile = files.Count;
                                        Console.WriteLine($"添加成功 {path}");
                                    } else
                                        Console.WriteLine("文件已存在");
                                else
                                    Console.WriteLine("不支持的后缀");
                                if (!(listfile is null) && File.Exists(listfile))
                                    save();
                            } else if (Directory.Exists(path)) {
                                var last_time = DateTime.Now.AddSeconds(-1);
                                void f(string p) {
                                    var t = DateTime.Now;
                                    if (t.Subtract(last_time).TotalMilliseconds > 33) {
                                        last_time = t;
                                        Console.Title = $"...{p}";
                                    }
                                    foreach (var file in Directory.GetFiles(p))
                                        if (Check(file))
                                            if (datas.Add(file)) {
                                                files.Add(file);
                                                Console.WriteLine($"+{file}");
                                            }
                                    foreach (var dir in Directory.GetDirectories(p))
                                        f(dir);
                                }
                                f(path);
                                Console.WriteLine($"成功添加{files.Count - nfile}个文件");
                                nfile = files.Count;
                                if (!(listfile is null) && File.Exists(listfile))
                                    save();
                            } else
                                Console.WriteLine("路径非法");
                        } else if (cmd.StartsWith("?")) {
                            var pattern = cmd.Substring(1);
                            try {
                                var f = pattern.StartsWith("\\") ?
                                    (new Regex(pattern.Substring(1), RegexOptions.Compiled | RegexOptions.ExplicitCapture).IsMatch) :
                                    (Func<string, bool>)(p => p.Contains(pattern));
                                var n = 0;
                                foreach (var file in files)
                                    if (f(file)) {
                                        ++n;
                                        Console.WriteLine(file);
                                    }
                                Console.WriteLine($"共{n}个符合");
                            } catch (Exception e) {
                                Console.WriteLine(e);
                            }
                        } else if (cmd.StartsWith("-")) {
                            var pattern = cmd.Substring(1);
                            try {
                                var f = pattern.StartsWith("\\") ?
                                    (new Regex(pattern.Substring(1), RegexOptions.Compiled | RegexOptions.ExplicitCapture).IsMatch) :
                                    (Func<string, bool>)(p => p.Contains(pattern));
                                var n = 0;
                                foreach (var file in files)
                                    if (f(file)) {
                                        ++n;
                                        Console.WriteLine($"-{file}");
                                        datas.Remove(file);
                                    }
                                files.RemoveRange(0, files.Count);
                                files.AddRange(datas);
                                nfile = files.Count;
                                Console.WriteLine($"共删除{n}个");
                                if (!(listfile is null) && File.Exists(listfile))
                                    save();
                            } catch (Exception e) {
                                Console.WriteLine(e);
                            }
                        } else if (cmd.StartsWith("=")) {
                            var pattern = cmd.Substring(1);
                            try {
                                toplay = pattern.StartsWith("\\") ?
                                    (new Regex(pattern.Substring(1), RegexOptions.Compiled | RegexOptions.ExplicitCapture).IsMatch) :
                                    (Predicate<string>)(p => p.Contains(pattern));
                                started = true;
                            } catch (Exception e) {
                                Console.WriteLine(e);
                            }
                        } else {
                            Console.WriteLine("未知命令");
                        }
                        break;
                }
                cmd = null;
                ch.ReleaseMutex();
                return true;
            }
            while (notquit && pcmd()) {
                if (!started) {
                    Console.Title = "等待 start 命令开始播放/使用 help 命令以获取帮助";
                    Thread.Sleep(320);
                    continue;
                }
                if (nfile <= 0) {
                    Console.Title = "文件列表是空的！！！";
                    Thread.Sleep(320);
                    continue;
                }
                var index = toplay is null ? ((1 + rand.Next(nfile) + RCutN(new Random(Environment.TickCount), (int)(DateTime.Now.Ticks % 257) + 2).Next(nfile)) % nfile) : files.FindIndex(toplay);
                toplay = null;
                if (index < 0) {
                    if (ch.WaitOne(1)) {
                        Console.WriteLine("找不到符合");
                        ch.ReleaseMutex();
                    }
                    continue;
                }
                var file = files[index];
                file = GetFullPath(file) ?? file;
                var h = Bass.BASS_StreamCreateFile(file, 0, 0, BASSFlag.BASS_SAMPLE_FLOAT);
                if (h is 0) {
                    if (ch.WaitOne(1)) {
                        Console.WriteLine($"读取文件失败:{Bass.BASS_ErrorGetCode()}|{file}");
                        ch.ReleaseMutex();
                    } else
                        Console.Title = $"读取文件失败:{Bass.BASS_ErrorGetCode()}|{file}";
                    continue;
                }
                playing = file;
                var total_time = Bass.BASS_ChannelBytes2Seconds(h, Bass.BASS_ChannelGetLength(h));
                var str_total_time = Sec2Str(total_time);
                var total_time2 = total_time * 0.95;
                total_time -= 0.125;
                last_pos = -1;
                Bass.BASS_ChannelPlay(h, true);
                while (pcmd() && toplay is null) {
                    var this_pos = Bass.BASS_ChannelBytes2Seconds(h, Bass.BASS_ChannelGetPosition(h));
                    if ((this_pos <= last_pos && this_pos > total_time2) || (this_pos >= total_time)) break;
                    last_pos = this_pos;
                    Console.Title = $"{Sec2Str(this_pos)}/{str_total_time} | {file}";
                    Thread.Sleep(320);
                }
                playing = null;
                Bass.BASS_ChannelStop(h);
                Bass.BASS_StreamFree(h);
            }
            Bass.BASS_Stop();
            Bass.BASS_Free();
        }
    }
}
