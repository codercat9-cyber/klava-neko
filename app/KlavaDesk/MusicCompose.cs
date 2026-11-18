using System.Text;
using System.Text.Json.Nodes;
using MeltySynth;
using NAudio.Wave;

namespace KlavaDesk;

/// <summary>
/// «Могу воспроизвести похожее» (друг 30.09, «Клава Певица.md»: символический путь — варьировать снятую партитуру
/// и прогнать через свои инструменты). Из разбора трека берутся темп, размер, тональность, прогрессия припева,
/// типичный такт ударных со свингом и оттяжкой, бас — по бочке, инструменты — по тому, что услышал AST; мелодия —
/// своя (ритм мотива — из голоса оригинала, опорные звуки аккорда на сильных долях, между ними — ходы по ладу).
/// Итог — MIDI (открывается в любой DAW) и WAV через SoundFont (MeltySynth, GeneralUser GS) — без сети и без Python.
/// </summary>
internal static class MusicCompose
{
    private const int Tpb = 480;   // тиков на долю

    private sealed record Chord(int Root, string Quality)
    {
        public int[] Tones => Quality switch
        {
            "m" => [0, 3, 7], "7" => [0, 4, 7, 10], "maj7" => [0, 4, 7, 11], "m7" => [0, 3, 7, 10], "dim" => [0, 3, 6],
            "sus4" => [0, 5, 7], "5" => [0, 7], _ => [0, 4, 7],
        };
        public bool Minor => Quality is "m" or "m7" or "dim";
        public override string ToString() => $"{Names[Root]}{Quality}";
    }

    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static Chord? ParseChord(string name)
    {
        if (name is "N" or "") return null;
        var s = name.Split('/')[0];
        var r = Array.IndexOf(Names, s.Length > 1 && s[1] == '#' ? s[..2] : s[..1]);
        if (r < 0) return null;
        return new Chord(r, s[(s.Length > 1 && s[1] == '#' ? 2 : 1)..]);
    }

    private readonly record struct Ev(long Tick, int Order, byte[] Data);

    /// <summary>Что сочинилось: MIDI, WAV, описание для неё и «песня» для сцены (доли, длительность).</summary>
    public sealed record Result(string Midi, string Wav, string Description, JsonObject Song);

    /// <summary>Похожая основа по разбору трека (analysis.json).</summary>
    public static Result Compose(string trackDir, string outDir, string soundFont, int seed)
    {
        var an = JsonNode.Parse(File.ReadAllText(Path.Combine(trackDir, "analysis.json")))!;
        var title = an["meta"]?["title"]?.GetValue<string>() ?? Path.GetFileName(trackDir);
        return Build(an, outDir, soundFont, seed, $"Похожая основа: {title}", null, "similar");
    }

    /// <summary>Настроения своей музыки: жанр (→ инструмент мелодии), гармония, темп, лад, прогрессия ступенями, ударные.</summary>
    private static readonly Dictionary<string, (string Genre, string Harm, string Mood, int Lo, int Hi, bool Minor, string[] Prog, string[] Drums, int Swing)> Moods = new()
    {
        ["happy"] = ("Pop music", "Piano", "Happy music", 112, 126, false, ["I", "V", "vi", "IV"], ["x.......x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x."], 0),
        ["sad"] = ("Classical music", "String section", "Sad music", 70, 80, true, ["i", "VI", "III", "VII"], ["x.......x.......", "........x.......", "x...x...x...x..."], 0),
        ["chill"] = ("Jazz", "Electric piano", "Tender music", 80, 90, false, ["ii7", "V7", "Imaj7", "vi7"], ["x.........x.....", "....x.......x...", "x.x.x.x.x.x.x.x."], 35),
        ["energetic"] = ("Electronic music", "Synthesizer", "Exciting music", 122, 130, true, ["i", "VI", "III", "VII"], ["x...x...x...x...", "....x.......x...", "..x...x...x...x."], 0),
        ["rock"] = ("Rock music", "Electric guitar", "Angry music", 132, 148, true, ["i5", "VI5", "VII5", "i5"], ["x.x.....x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x."], 0),
    };

    /// <summary>
    /// Своя музыка Клавы (друг 30.09 из её хотелок: «хочу сочинять — мурлыкнула мелодию, и она заиграла»): без исходного
    /// трека — настроение даёт темп, лад, прогрессию, грув и инструменты, мелодия — своя. Играет её за синтезатором.
    /// </summary>
    public static Result Original(string outDir, string soundFont, int seed, string mood)
    {
        var rnd = new Random(seed);
        var key = Moods.ContainsKey(mood) ? mood : Moods.Keys.ElementAt(rnd.Next(Moods.Count));
        var m = Moods[key];
        var bpm = rnd.Next(m.Lo, m.Hi + 1);
        var tonic = m.Minor ? new[] { 9, 2, 4, 7 }[rnd.Next(4)] : new[] { 0, 2, 5, 7 }[rnd.Next(4)];
        int Degree(string roman)
        {
            var r = roman.TrimEnd('7', '5').Replace("maj", "");
            return r.ToUpperInvariant() switch { "I" => 0, "II" => 2, "III" => m.Minor ? 3 : 4, "IV" => 5, "V" => 7, "VI" => m.Minor ? 8 : 9, "VII" => m.Minor ? 10 : 11, _ => 0 };
        }
        string Quality(string roman)
        {
            var core = roman.Replace("maj7", "").TrimEnd('7', '5');
            var minor = core.Length > 0 && char.IsLower(core[0]);
            if (roman.EndsWith('5')) return "5";
            if (roman.EndsWith("maj7")) return "maj7";
            if (roman.EndsWith('7')) return minor ? "m7" : "7";
            return minor ? "m" : "";
        }
        var beat = 60.0 / bpm;
        var bars = new JsonArray();
        var chords = new JsonArray();
        for (var b = 0; b < 4; b++)
        {
            var arr = new JsonArray();
            for (var i = 0; i < 4; i++) arr.Add(Math.Round((b * 4 + i) * beat, 3));
            bars.Add(arr);
            var roman = m.Prog[b];
            chords.Add(new JsonArray(Math.Round(b * 4 * beat, 3), Math.Round((b + 1) * 4 * beat, 3), Names[(tonic + Degree(roman)) % 12] + Quality(roman)));
        }
        JsonArray Row(params string[] xs) => new(xs.Select(x => (JsonNode?)new JsonArray(x)).ToArray());
        var an = new JsonObject
        {
            ["tempo"] = new JsonObject { ["bpm"] = (double)bpm, ["meter"] = 4 },
            ["drums"] = new JsonObject { ["subdivision"] = 4, ["swing8"] = (double)m.Swing, ["bars"] = new JsonArray(), ["template"] = new JsonArray(m.Drums[0], m.Drums[1], m.Drums[2]) },
            ["key"] = new JsonObject { ["tonic"] = Names[tonic], ["mode"] = m.Minor ? "minor" : "major" },
            ["sections"] = new JsonArray(new JsonObject { ["name"] = "припев 1", ["label"] = "A", ["bars"] = new JsonArray(0, 4) }),
            ["bars"] = bars,
            ["chords"] = chords,
            ["labels"] = new JsonObject { ["instruments"] = Row(m.Harm, "Bass guitar", "Drum kit"), ["genre"] = Row(m.Genre), ["mood"] = Row(m.Mood) },
            ["notes"] = new JsonObject { ["vocals"] = new JsonArray(), ["melody"] = new JsonArray(), ["other"] = new JsonArray() },
            ["duration"] = 16 * beat,
        };
        var moodRu = key switch { "happy" => "весёлая", "sad" => "грустная", "chill" => "чилловая", "energetic" => "заводная", _ => "роковая" };
        return Build(an, outDir, soundFont, seed, $"Своя мелодия Клавы ({moodRu})", "synth", "own");
    }

    /// <summary>Общее ядро: разбор (или его подобие) → MIDI и звук. pose — как её показать на сцене (synth — играет).</summary>
    private static Result Build(JsonNode an, string outDir, string soundFont, int seed, string title, string? pose, string name)
    {
        var rnd = new Random(seed);
        var notes = new StringBuilder();
        var bpm = an["tempo"]?["bpm"]?.GetValue<double>() ?? 110;
        var meter = an["tempo"]?["meter"]?.GetValue<int>() ?? 4;
        var sub = an["drums"]?["subdivision"]?.GetValue<int>() ?? 4;
        var sure = meter is 3 or 4 && bpm is >= 60 and <= 165;
        if (bpm > 165) bpm /= 2;         // быстрый счёт у медленной песни — дробление доли, играем в половину
        if (bpm < 60) bpm *= 2;
        if (meter is not (3 or 4)) meter = 4;
        var keyPc = Array.IndexOf(Names, an["key"]?["tonic"]?.GetValue<string>() ?? "C");
        var minorKey = an["key"]?["mode"]?.GetValue<string>() == "minor";
        int[] scale = minorKey ? [0, 2, 3, 5, 7, 8, 10] : [0, 2, 4, 5, 7, 9, 11];

        // часть-образец: припев, иначе самая повторяемая
        var secs = an["sections"]?.AsArray() ?? [];
        var sec = secs.FirstOrDefault(s => s!["name"]?.GetValue<string>().StartsWith("припев") == true)
                  ?? secs.GroupBy(s => s!["label"]!.GetValue<string>()).OrderByDescending(g => g.Count()).FirstOrDefault()?.First();
        var bars = an["bars"]?.AsArray() ?? [];
        var (b0, b1) = sec?["bars"] is JsonArray ba ? (ba[0]!.GetValue<int>(), ba[1]!.GetValue<int>()) : (0, bars.Count);

        // прогрессия: аккорд на начале такта (и в середине, если сменился), не больше 8 тактов
        var chords = (an["chords"]?.AsArray() ?? []).Select(c => (T0: c![0]!.GetValue<double>(), T1: c[1]!.GetValue<double>(), C: ParseChord(c[2]!.GetValue<string>()))).ToList();
        Chord At(double t) => chords.FirstOrDefault(c => c.T0 <= t + 0.03 && t < c.T1).C ?? new Chord(keyPc, minorKey ? "m" : "");
        var prog = new List<(Chord A, Chord B)>();
        for (var b = b0; b < Math.Min(b1, b0 + 8) && b < bars.Count; b++)
        {
            var beats = bars[b]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
            var mid = beats.Length > 2 ? beats[beats.Length / 2] : beats[0];
            prog.Add((At(beats[0]), At(mid)));
        }
        if (prog.Count == 0) prog.Add((new Chord(keyPc, minorKey ? "m" : ""), new Chord(keyPc, minorKey ? "m" : "")));
        while (prog.Count < 4) prog.AddRange(prog.ToList());
        if (prog.Count > 4 && prog.Count < 8) prog = prog.Take(4).ToList();
        var chordsText = string.Join(" ", prog.Select(p => p.A.ToString() + (p.B != p.A ? "/" + p.B : "")));
        notes.Append(name == "similar" ? $"прогрессия оригинала ({(sec?["name"] ?? "часть")}): {chordsText}" : $"своя прогрессия: {chordsText}");

        // вариация: треть тактов (кроме первого и последнего) — параллельный аккорд (мажор ↔ его параллельный минор)
        Chord Swap(Chord c) => c.Quality is "5" or "sus4" ? c : c.Minor ? new Chord((c.Root + 3) % 12, "") : new Chord((c.Root + 9) % 12, "m");
        List<(Chord A, Chord B)> progB = [.. prog.Select((p, i) => i > 0 && i < prog.Count - 1 && rnd.NextDouble() < 0.35 ? (A: Swap(p.A), B: p.A == p.B ? Swap(p.A) : p.B) : p)];
        notes.Append($"; моя вариация: {string.Join(" ", progB.Select(p => p.A.ToString() + (p.B != p.A ? "/" + p.B : "")))}");

        // ударные: типичный такт части (голосование по шагам), иначе — шаблон
        var (pk, ps, ph) = DrumPattern(an, b0, b1, meter * sub, sure);
        var fromTemplate = !sure || pk.Length == 0;
        if (fromTemplate)
        {
            sub = 4;
            (pk, ps, ph) = an["drums"]?["template"] is JsonArray tpl && tpl.Count == 3
                ? (tpl[0]!.GetValue<string>(), tpl[1]!.GetValue<string>(), tpl[2]!.GetValue<string>())
                : meter == 3
                ? ("x...........", "....x...x...", "x.x.x.x.x.x.")
                : bpm < 90 ? ("x.........x.....", "........x.......", "x...x...x...x...") : ("x.......x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x.");
        }
        notes.Append($"; грув{(fromTemplate && !sure ? " (слух не уверен в размере — шаблон)" : "")}: бочка {pk} малый {ps} хэт {ph}");
        var swing = sub == 4 ? Math.Clamp(an["drums"]?["swing8"]?.GetValue<double?>() ?? 0, 0, 100) : 0;
        var snareLag = an["drums"]?["snare_backbeat_ms"]?.GetValue<double?>() ?? 0;
        var stepTicks = Tpb / sub;
        long Ms(double ms) => (long)(ms / 1000 * bpm / 60 * Tpb);
        long Swing(int step) => sub == 4 && step % 4 == 2 ? (long)(swing / 100 * Tpb / 6) : 0;

        // инструменты — по тому, что услышал слух
        var inst = (an["labels"]?["instruments"]?.AsArray() ?? []).Select(r => r![0]!.GetValue<string>()).ToList();
        var genre = (an["labels"]?["genre"]?.AsArray() ?? []).Select(r => r![0]!.GetValue<string>()).FirstOrDefault() ?? "";
        var rock = genre is "Rock music" or "Heavy metal" or "Grunge" or "Punk rock" or "Progressive rock" or "Psychedelic rock";
        int Pick(params (string Label, int Prog)[] map) => map.FirstOrDefault(m => inst.Take(8).Contains(m.Label)).Prog;
        var bassProg = Pick(("Bass guitar", 33), ("Synthesizer", 38), ("Double bass", 32));
        if (bassProg == 0) bassProg = 33;
        var harmProg = Pick(("Electric guitar", rock ? 30 : 27), ("Acoustic guitar", 25), ("Piano", 0), ("Electric piano", 4), ("Hammond organ", 16),
                            ("Organ", 19), ("String section", 48), ("Orchestra", 48), ("Synthesizer", 89), ("Guitar", rock ? 29 : 25));
        var leadProg = rock ? 29 : genre is "Classical music" or "Opera" ? 40 : genre is "Jazz" or "Soul music" or "Blues" ? 66
            : genre.Contains("Electronic") || genre is "House music" or "Techno" or "Trance music" ? 81 : 73;
        var densOther = (an["notes"]?["other"]?.AsArray().Count ?? 0) / Math.Max(1.0, an["duration"]?.GetValue<double>() ?? 1);
        var arpeggio = densOther > 3 && harmProg is 0 or 4 or 25 or 27 or 46;
        notes.Append($"; инструменты (GM): бас {bassProg}, гармония {harmProg}{(arpeggio ? " перебором" : "")}, мелодия {leadProg}");

        // ритм мотива — из голоса (или верхнего голоса) оригинала в этой части, по восьмым
        var mel = (an["notes"]?["vocals"]?.AsArray() is { Count: > 8 } v ? v : an["notes"]?["melody"]?.AsArray() ?? []).ToList();
        var barLen = meter * Tpb;
        var motif = new List<(long At, long Len)>();
        if (b0 < bars.Count && mel.Count > 0)
        {
            var t0 = bars[b0]![0]!.GetValue<double>();
            var beatSec = 60 / (an["tempo"]?["bpm"]?.GetValue<double>() ?? bpm);
            foreach (var n in mel)
            {
                var s = n![0]!.GetValue<double>() - t0;
                if (s < 0) continue;
                var at = (long)Math.Round(s / beatSec * 2) * (Tpb / 2);
                if (at >= 2 * barLen) break;
                var len = Math.Max(Tpb / 2, (long)Math.Round((n[1]!.GetValue<double>() - n[0]!.GetValue<double>()) / beatSec * 2) * (Tpb / 2));
                if (motif.Count == 0 || at > motif[^1].At) motif.Add((at, len));
            }
        }
        if (motif.Count < 4) motif = [(0, Tpb), (Tpb, Tpb / 2), (Tpb * 3 / 2, Tpb / 2), (2 * Tpb, Tpb * 2), (barLen, Tpb), (barLen + Tpb, Tpb), (barLen + 2 * Tpb, Tpb * 2)];

        // ---------- ноты ----------
        var ev = new List<Ev>();
        var melody = new List<(long At, long Len, int Pitch)>();   // ноты мелодии — на сцену (лапки по клавишам)
        void Note(int ch, long at, long len, int pitch, int vel)
        {
            at = Math.Max(0, at);
            if (ch == 2) melody.Add((at, Math.Max(10, len), pitch));
            ev.Add(new Ev(at, 1, [(byte)(0x90 | ch), (byte)Math.Clamp(pitch, 0, 127), (byte)Math.Clamp(vel, 1, 127)]));
            ev.Add(new Ev(at + Math.Max(10, len), 0, [(byte)(0x80 | ch), (byte)Math.Clamp(pitch, 0, 127), 0]));
        }
        void Program(int ch, int prog) => ev.Add(new Ev(0, -1, [(byte)(0xC0 | ch), (byte)prog]));
        Program(0, bassProg);
        Program(1, harmProg);
        Program(2, leadProg);
        ev.Add(new Ev(0, -1, [0xB1, 7, 88]));   // гармония чуть тише
        ev.Add(new Ev(0, -1, [0xB2, 91, 50]));  // мелодии — зал
        ev.Add(new Ev(0, -1, [0xB1, 91, 35]));

        // форма: вступление 4 (без мелодии, барабаны со 3-го), A 8, B 8 (вариация), концовка 4
        var plan = new List<(string Part, int Bar)>();
        for (var i = 0; i < 4; i++) plan.Add(("intro", i));
        for (var i = 0; i < 8; i++) plan.Add(("A", i));
        for (var i = 0; i < 8; i++) plan.Add(("B", i));
        for (var i = 0; i < 4; i++) plan.Add(("outro", i));
        int[]? voicing = null;
        var lastMel = 60 + (keyPc + 12 - 60 % 12) % 12;
        if (lastMel < 60) lastMel += 12;
        for (var bi = 0; bi < plan.Count; bi++)
        {
            var (part, pb) = plan[bi];
            var bar = (long)bi * barLen;
            var pr = (part == "B" ? progB : prog)[pb % prog.Count];
            Chord ChordAt(long tickInBar) => tickInBar < barLen / 2 ? pr.A : pr.B;
            var drums = part != "intro" || pb >= 2;
            var fillBar = pb == 7 || (part == "intro" && pb == 3);
            // ударные
            if (drums)
            {
                if ((part is "A" or "B" or "outro") && pb == 0) Note(9, bar, Tpb, 49, 105);
                for (var s = 0; s < meter * sub; s++)
                {
                    var at = bar + s * stepTicks + Swing(s);
                    var accent = s % sub == 0;
                    if (s < pk.Length && pk[s] == 'x' && !(part == "outro" && pb == 3 && s > 0)) Note(9, at + rnd.Next(-6, 7), 60, 36, 100 + rnd.Next(-8, 9));
                    if (s < ps.Length && ps[s] == 'x') Note(9, at + Ms(snareLag) + rnd.Next(-5, 6), 60, 38, 104 + rnd.Next(-8, 9));
                    if (s < ph.Length && ph[s] == 'x' && !(fillBar && s >= meter * sub / 2)) Note(9, at + rnd.Next(-8, 9), 40, 42, (accent ? 82 : 62) + rnd.Next(-10, 11));
                    // сбивка в последнем такте фразы — малый шестнадцатыми на второй половине
                    if (fillBar && s >= meter * sub / 2 && s % Math.Max(1, sub / 2) == 0) Note(9, at, 40, s % sub == 0 ? 38 : 45, 80 + s * 2);
                }
            }
            // бас: корень аккорда на ударах бочки (и всегда на сильной доле), перед сменой аккорда — подход полутоном
            var kicks = Enumerable.Range(0, meter * sub).Where(s => s == 0 || (s < pk.Length && pk[s] == 'x')).ToList();
            if (!drums) kicks = [0, meter * sub / 2];
            for (var k = 0; k < kicks.Count; k++)
            {
                var s = kicks[k];
                var at = bar + s * stepTicks + Swing(s);
                var end = k + 1 < kicks.Count ? bar + kicks[k + 1] * stepTicks : bar + barLen;
                var c = ChordAt(s * stepTicks);
                var p = 28 + (c.Root + 12 - 4) % 12;
                if (k == kicks.Count - 1 && end - at >= Tpb && rnd.NextDouble() < 0.3)
                {
                    var next = (part == "B" ? progB : prog)[(pb + 1) % prog.Count].A;
                    var np = 28 + (next.Root + 12 - 4) % 12;
                    Note(0, at, end - at - Tpb / 2 - 10, p, 96);
                    Note(0, end - Tpb / 2, Tpb / 2 - 10, np + (rnd.Next(2) == 0 ? -1 : 1), 88);
                    continue;
                }
                Note(0, at, end - at - 10, p, 96 + rnd.Next(-6, 7));
            }
            // гармония: ближайшее обращение к прошлому аккорду (голоса не прыгают), рок — квинты восьмыми
            for (var half = 0; half < 2; half++)
            {
                var c = half == 0 ? pr.A : pr.B;
                if (half == 1 && pr.A == pr.B && !arpeggio && harmProg != 30) continue;
                var at = bar + half * barLen / 2;
                var len = pr.A == pr.B && half == 0 && !arpeggio && harmProg != 30 ? barLen : barLen / 2;
                var tones = c.Tones.Select(t => (c.Root + t) % 12).ToArray();
                if (harmProg == 30)
                {
                    var r = 40 + (c.Root + 12 - 4) % 12;
                    for (var e = 0; e < len / (Tpb / 2); e++)
                        foreach (var p in new[] { r, r + 7, r + 12 }) Note(1, at + e * Tpb / 2 + Swing(e * 2 * sub / 4), Tpb / 2 - 30, p, e % 2 == 0 ? 92 : 74);
                    continue;
                }
                voicing = Voice(tones, voicing);
                if (arpeggio)
                {
                    var arp = new[] { voicing[0] - 12, voicing[^1] - 12, voicing[0], voicing[1 % voicing.Length], voicing[^1], voicing[1 % voicing.Length] };
                    for (var e = 0; e < len / (Tpb / 2); e++) Note(1, at + e * Tpb / 2 + Swing(e * 2), Tpb / 2 + 60, arp[e % arp.Length], 70 + rnd.Next(-6, 7));
                }
                else foreach (var p in voicing) Note(1, at, len - 20, p, part == "intro" ? 62 : 72);
            }
            // мелодия: мотив в 2 такта; на сильных долях — звук аккорда, между — ход по ладу; B — на терцию выше
            if (part is "A" or "B" || (part == "outro" && pb < 2))
            {
                var mBar = (long)(pb % 2) * barLen;
                foreach (var (mAt, mLen) in motif.Where(m => m.At >= mBar && m.At < mBar + barLen))
                {
                    var inBar = mAt - mBar;
                    var c = ChordAt(inBar);
                    var strong = inBar % Tpb == 0 && (inBar / Tpb) % 2 == 0;
                    int target;
                    if (strong || rnd.NextDouble() < 0.35)
                    {
                        var cands = c.Tones.Select(t => (c.Root + t) % 12).SelectMany(pc => new[] { 48 + pc, 60 + pc, 72 + pc }).Where(p => p is >= 57 and <= 79);
                        target = cands.OrderBy(p => Math.Abs(p - lastMel) + rnd.NextDouble() * 3).First();
                    }
                    else
                    {
                        var dir = rnd.Next(2) == 0 ? -1 : 1;
                        target = lastMel + dir;
                        while (!scale.Contains(((target - keyPc) % 12 + 12) % 12)) target += dir;
                    }
                    if (part == "B" && pb >= 4) target = Math.Min(81, target + (scale.Contains(((target + 3 - keyPc) % 12 + 12) % 12) ? 3 : 4));
                    // последний звук фразы — тоника
                    if (part == "outro" && pb == 1 && mAt == motif.Where(m => m.At >= mBar && m.At < mBar + barLen).Max(m => m.At))
                        target = new[] { 60, 72 }.Select(o => o + keyPc % 12).OrderBy(p => Math.Abs(p - lastMel)).First();
                    lastMel = target;
                    Note(2, bar + inBar + rnd.Next(-8, 9), Math.Min(mLen, barLen - inBar) - 20, target, (strong ? 96 : 84) + rnd.Next(-6, 7));
                }
            }
        }
        var totalTicks = (long)plan.Count * barLen + Tpb * 2;

        // ---------- файлы ----------
        Directory.CreateDirectory(outDir);
        var midi = Path.Combine(outDir, $"{name}.mid");
        WriteMidi(midi, ev, bpm, meter, totalTicks);
        var wav = Path.Combine(outDir, $"{name}.wav");
        var seconds = Render(midi, soundFont, wav, totalTicks / (double)Tpb * 60 / bpm + 2.5);
        var beatsOut = new JsonArray();
        var downs = new JsonArray();
        for (var i = 0; i < plan.Count * meter; i++)
        {
            var t = Math.Round(i * 60 / bpm, 3);
            beatsOut.Add(t);
            if (i % meter == 0) downs.Add(t);
        }
        double Sec(long tick) => Math.Round(tick / (double)Tpb * 60 / bpm, 3);
        var keys = new JsonArray([.. melody.OrderBy(n => n.At).Select(n => (JsonNode?)new JsonArray(Sec(n.At), Sec(n.At + n.Len), n.Pitch))]);
        var song = new JsonObject
        {
            ["mode"] = "track", ["dir"] = outDir, ["mix"] = $"{name}.wav", ["title"] = title, ["duration"] = seconds,
            ["beats"] = beatsOut, ["downbeats"] = downs, ["lines"] = new JsonArray(), ["keys"] = keys, ["mood"] = an["labels"]?["mood"]?.AsArray()?.Take(1)
                .Select(r => (JsonNode?)JsonValue.Create(r![0]!.GetValue<string>())).Aggregate(new JsonArray(), (a, x) => { a.Add(x); return a; }),
        };
        if (pose is not null) song["pose"] = pose;
        File.WriteAllText(Path.Combine(outDir, $"{name}.json"), song.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var desc = $"Темп {bpm:F0} BPM, {meter}/4, тональность {Names[keyPc]} {(minorKey ? "минор" : "мажор")}, 24 такта: вступление, A, B, концовка. {notes}.";
        File.WriteAllText(Path.Combine(outDir, $"{name}.txt"), desc);
        return new Result(midi, wav, desc, song);
    }

    // Тема 24-го каприса Паганини (ля минор, 2/4, общественное достояние): (доля, длительность в долях, midi).
    // Первая половина — мотив «A · AC BA · E — E · G#F#E», вторая — ход вверх от A5 и каденция на A–C–E.
    private static readonly (double At, double Len, int Pitch)[] CapriceHalf1 =
    [
        (0, 0.5, 69), (0.75, 0.25, 69), (1, 0.25, 69), (1.25, 0.25, 72), (1.5, 0.25, 71), (1.75, 0.25, 69),
        (2, 0.5, 76), (2.75, 0.25, 64), (3, 0.25, 64), (3.25, 0.25, 68), (3.5, 0.25, 66), (3.75, 0.25, 64),
        (4, 0.5, 69), (4.75, 0.25, 69), (5, 0.25, 69), (5.25, 0.25, 72), (5.5, 0.25, 71), (5.75, 0.25, 69),
        (6, 1, 76), (7, 0.5, 64),
        (8, 0.5, 69), (8.75, 0.25, 69), (9, 0.25, 69), (9.25, 0.25, 72), (9.5, 0.25, 71), (9.75, 0.25, 69),
        (10, 0.5, 76), (10.75, 0.25, 64), (11, 0.25, 64), (11.25, 0.25, 68), (11.5, 0.25, 66), (11.75, 0.25, 64),
        (12, 0.5, 69), (12.75, 0.25, 69), (13, 0.25, 69), (13.25, 0.25, 72), (13.5, 0.25, 71), (13.75, 0.25, 69),
        (14, 1, 76), (15, 0.5, 64),
    ];
    private static readonly (double At, double Len, int Pitch)[] CapriceHalf2 =
    [
        (0, 0.5, 81), (0.75, 0.25, 81), (1, 0.25, 81), (1.25, 0.25, 82), (1.5, 0.25, 81), (1.75, 0.25, 79),
        (2, 0.5, 77), (2.75, 0.25, 74), (3, 0.25, 74), (3.25, 0.25, 77), (3.5, 0.25, 76), (3.75, 0.25, 74),
        (4, 0.5, 79), (4.75, 0.25, 79), (5, 0.25, 79), (5.25, 0.25, 81), (5.5, 0.25, 79), (5.75, 0.25, 77),
        (6, 0.5, 76), (6.75, 0.25, 72), (7, 0.25, 72), (7.25, 0.25, 76), (7.5, 0.25, 74), (7.75, 0.25, 72),
        (8, 0.5, 77), (8.75, 0.25, 71), (9, 0.25, 71), (9.25, 0.25, 74), (9.5, 0.25, 72), (9.75, 0.25, 71),
        (10, 0.5, 76), (10.75, 0.25, 69), (11, 0.25, 69), (11.25, 0.25, 72), (11.5, 0.25, 71), (11.75, 0.25, 69),
        (12, 0.5, 65), (12.75, 0.25, 75), (13, 0.25, 64), (13.25, 0.25, 76), (13.5, 0.25, 74), (13.75, 0.25, 71),
        (14, 1, 69), (15, 0.5, 81),
    ];

    /// <summary>
    /// Скрипка «в стиле Паганини» (обещание Кота 30.09: «научу её играть на скрипке, как Паганини»): тема 24-го каприса —
    /// первая половина дважды, вторая, финальный аккорд A–C–E; живость — в штрихах (громкость по долям, лёгкий разброс).
    /// </summary>
    public static Result Violin(string outDir, string soundFont, int seed)
    {
        var rnd = new Random(seed);
        const double bpm = 112;
        var ev = new List<Ev>();
        var keys = new JsonArray();
        long T(double beats) => (long)Math.Round(beats * Tpb);
        void Note(double at, double len, int pitch, int vel)
        {
            var a = Math.Max(0, T(at) + rnd.Next(-6, 7));
            var l = Math.Max(30, T(len) - 18);
            ev.Add(new Ev(a, 1, [0x90, (byte)pitch, (byte)Math.Clamp(vel, 1, 127)]));
            ev.Add(new Ev(a + l, 0, [0x80, (byte)pitch, 0]));
            keys.Add(new JsonArray(Math.Round(a / (double)Tpb * 60 / bpm, 3), Math.Round((a + l) / (double)Tpb * 60 / bpm, 3), pitch));
        }
        ev.Add(new Ev(0, -1, [0xC0, 40]));
        ev.Add(new Ev(0, -1, [0xB0, 91, 70]));   // зал
        var at = 0.0;
        foreach (var half in new[] { CapriceHalf1, CapriceHalf1, CapriceHalf2 })
        {
            foreach (var (a, len, p) in half)
            {
                var onBeat = Math.Abs(a - Math.Round(a)) < 1e-6;
                Note(at + a, len, p, (onBeat ? 104 : 88) + rnd.Next(-6, 7));
            }
            at += 16;
        }
        foreach (var p in new[] { 81, 84, 88 }) Note(at, 2, p, 110);
        var total = T(at + 3);
        Directory.CreateDirectory(outDir);
        var midi = Path.Combine(outDir, "violin.mid");
        WriteMidi(midi, ev, bpm, 2, total);
        var wav = Path.Combine(outDir, "violin.wav");
        var seconds = Render(midi, soundFont, wav, total / (double)Tpb * 60 / bpm + 2.5);
        var beats = new JsonArray();
        var downs = new JsonArray();
        for (var i = 0; i < at + 2; i++)
        {
            beats.Add(Math.Round(i * 60 / bpm, 3));
            if (i % 2 == 0) downs.Add(Math.Round(i * 60 / bpm, 3));
        }
        var song = new JsonObject
        {
            ["mode"] = "track", ["dir"] = outDir, ["mix"] = "violin.wav", ["title"] = "Паганини, Каприс №24 — тема", ["duration"] = seconds,
            ["beats"] = beats, ["downbeats"] = downs, ["lines"] = new JsonArray(), ["keys"] = keys, ["pose"] = "violin",
            ["mood"] = new JsonArray("Exciting music"),
        };
        File.WriteAllText(Path.Combine(outDir, "violin.json"), song.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return new Result(midi, wav, "Паганини, 24-й каприс ля минор: тема (первая половина дважды, вторая, финальный аккорд A–C–E), скрипка из звукового банка.", song);
    }

    /// <summary>Обращение аккорда в районе G3–C5, ближе всего к прошлому (плавное голосоведение).</summary>
    private static int[] Voice(int[] pcs, int[]? prev)
    {
        var best = Array.Empty<int>();
        var bestCost = double.MaxValue;
        var basePcs = pcs.Length == 2 ? [pcs[0], pcs[1], pcs[0]] : pcs.Take(4).ToArray();
        for (var inv = 0; inv < basePcs.Length; inv++)
            for (var oct = 52; oct <= 64; oct += 12)
            {
                var v = new List<int>();
                var last = oct - 1;
                for (var k = 0; k < basePcs.Length; k++)
                {
                    var pc = basePcs[(inv + k) % basePcs.Length];
                    var p = last + 1 + ((pc - (last + 1)) % 12 + 12) % 12;
                    v.Add(p);
                    last = p;
                }
                if (v[0] < 53 || v[^1] > 76) continue;
                var cost = prev is null ? Math.Abs(v.Average() - 64) : v.Zip(prev).Sum(z => Math.Abs(z.First - z.Second)) + Math.Abs(v.Count - prev.Length) * 3;
                if (cost < bestCost) { bestCost = cost; best = [.. v]; }
            }
        return best.Length > 0 ? best : [.. pcs.Select(pc => 60 + pc)];
    }

    private static (string K, string S, string H) DrumPattern(JsonNode an, int b0, int b1, int steps, bool sure)
    {
        var rows = (an["drums"]?["bars"]?.AsArray() ?? []).Where(r => r!["bar"]!.GetValue<int>() >= b0 && r["bar"]!.GetValue<int>() < b1
                                                                     && r["steps"]!.GetValue<int>() == steps).ToList();
        if (!sure || rows.Count < 2) return ("", "", "");
        string Vote(string k)
        {
            var sb = new StringBuilder();
            for (var s = 0; s < steps; s++)
                sb.Append(rows.Count(r => r![k]!.GetValue<string>()[s] == 'x') >= 0.45 * rows.Count ? 'x' : '.');
            return sb.ToString();
        }
        var (kk, ss, hh) = (Vote("K"), Vote("S"), Vote("H"));
        if (!kk.Contains('x')) kk = "x" + new string('.', steps - 1);
        if (!hh.Contains('x')) hh = string.Concat(Enumerable.Range(0, steps).Select(s => s % 2 == 0 ? 'x' : '.'));
        return (kk, ss, hh);
    }

    // ---------- MIDI и звук ----------

    private static void WriteMidi(string path, List<Ev> events, double bpm, int meter, long end)
    {
        using var f = new BinaryWriter(File.Create(path));
        void Be32(int v) { f.Write((byte)(v >> 24)); f.Write((byte)(v >> 16)); f.Write((byte)(v >> 8)); f.Write((byte)v); }
        void Be16(int v) { f.Write((byte)(v >> 8)); f.Write((byte)v); }
        f.Write("MThd"u8);
        Be32(6);
        Be16(0);
        Be16(1);
        Be16(Tpb);
        var body = new MemoryStream();
        void Var(long v)
        {
            var buf = new Stack<byte>();
            buf.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) buf.Push((byte)((v & 0x7F) | 0x80));
            foreach (var b in buf) body.WriteByte(b);
        }
        var usPerBeat = (int)(60_000_000 / bpm);
        Var(0); body.Write([0xFF, 0x51, 0x03, (byte)(usPerBeat >> 16), (byte)(usPerBeat >> 8), (byte)usPerBeat]);
        Var(0); body.Write([0xFF, 0x58, 0x04, (byte)meter, 2, 24, 8]);
        long t = 0;
        foreach (var e in events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
        {
            Var(e.Tick - t);
            body.Write(e.Data);
            t = e.Tick;
        }
        Var(Math.Max(0, end - t)); body.Write([0xFF, 0x2F, 0x00]);
        f.Write("MTrk"u8);
        Be32((int)body.Length);
        f.Write(body.ToArray());
    }

    /// <summary>MIDI → WAV 44,1 кГц через SoundFont; пик −1 дБ. Возвращает длительность, с.</summary>
    private static double Render(string midiPath, string soundFont, string wavPath, double seconds)
    {
        const int sr = 44100;
        var synth = new Synthesizer(soundFont, sr);
        var seq = new MidiFileSequencer(synth);
        seq.Play(new MidiFile(midiPath), false);
        var n = (int)(seconds * sr);
        var left = new float[n];
        var right = new float[n];
        seq.Render(left, right);
        var peak = 1e-6f;
        for (var i = 0; i < n; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        var g = 0.89f / peak;
        using var w = new WaveFileWriter(wavPath, new WaveFormat(sr, 16, 2));
        var buf = new byte[4 * 4096];
        for (var i = 0; i < n; i += 4096)
        {
            var m = Math.Min(4096, n - i);
            for (var k = 0; k < m; k++)
            {
                BitConverter.TryWriteBytes(buf.AsSpan(k * 4), (short)Math.Clamp(left[i + k] * g * 32767, -32768, 32767));
                BitConverter.TryWriteBytes(buf.AsSpan(k * 4 + 2), (short)Math.Clamp(right[i + k] * g * 32767, -32768, 32767));
            }
            w.Write(buf, 0, m * 4);
        }
        return n / (double)sr;
    }
}
