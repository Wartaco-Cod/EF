using TacoFactory.Client.Audio;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

namespace TacoFactory.Client;

/// <summary>Which sound goes with which event, and which music with which place.</summary>
public sealed partial class GameClient
{
    private AudioEngine? _audio;
    private double _bossMusicUntil;

    private void InitAudio(string root)
    {
        _audio = new AudioEngine(root) { MusicVolume = _settings.MusicVolume, EffectsVolume = _settings.EffectsVolume };
        if (!_audio.Enabled) Console.WriteLine($"[client] no sound: {_audio.Problem}");
    }

    private void Sfx(string name, float volume = 1) => _audio?.Play(name, volume);

    /// <summary>A sound where an entity is, relative to you.</summary>
    private void SfxAt(string name, int entityId, float volume = 1)
    {
        if (_audio is null || !_conn.Entities.TryGetValue(entityId, out var e) || !_conn.Entities.TryGetValue(_conn.YourId, out var me)) return;
        var d = e.Current.Pos - me.Current.Pos;
        _audio.PlayAt(name, d.X, d.Y, volume);
    }

    private void SoundFor(SimEvent e)
    {
        if (_audio is not { Enabled: true }) return;
        int me = _conn.YourId;
        switch (e.Type)
        {
            case SimEventType.Damage when e.Value > 0:
                if (e.Target == me) Sfx("hurt", 0.8f);
                else SfxAt(e.Text == "crit" ? "crit" : "hit", e.Target, e.Source == me ? 0.9f : 0.45f);
                break;
            case SimEventType.SkillUsed:
                string sound = "swing";
                if (_data.Spells.TryGetValue(e.Text, out var spell))
                    sound = spell.Kind switch
                    {
                        SpellKind.Bolt => "cast_bolt", SpellKind.Burst => "cast_burst", SpellKind.Nova => "cast_nova",
                        SpellKind.Heal or SpellKind.HealArea => "cast_heal", SpellKind.BuffSelf or SpellKind.BuffArea => "cast_buff",
                        SpellKind.Drain => "cast_drain", SpellKind.Blink => "cast_blink", SpellKind.Revive => "cast_revive", _ => "swing",
                    };
                SfxAt(sound, e.Source, e.Source == me ? 0.8f : 0.4f);
                break;
            case SimEventType.Death:
                if (e.Target == me) Sfx("player_die");
                else SfxAt("monster_die", e.Target, 0.6f);
                break;
            case SimEventType.LevelUp when e.Source == me: Sfx("level_up"); break;
            case SimEventType.MasteryUp when e.Source == me: Sfx("mastery_up", 0.7f); break;
            case SimEventType.LootPicked when e.Source == me: Sfx(e.Text == "gold" ? "gold" : "loot", 0.7f); break;
            case SimEventType.Blocked: SfxAt("block", e.Target, 0.7f); break;
            case SimEventType.Telegraph: Sfx("telegraph", 0.6f); break;
            case SimEventType.BossPhase:
                Sfx("boss_phase", 0.8f);
                _bossMusicUntil = Now + 120;
                break;
            case SimEventType.UniqueKilled: _bossMusicUntil = 0; break;
            case SimEventType.DuelStarted when e.Source == me || e.Target == me: Sfx("duel_start"); break;
            case SimEventType.Revived when e.Target == me: Sfx("cast_revive"); break;
            case SimEventType.Alarm when e.Target == me: Sfx("alarm"); break;
            case SimEventType.ZoneCleared: Sfx("waypoint", 0.7f); break;
        }
    }

    /// <summary>The track for where you are: the zone's own, else the act's town or field music; boss fights have their own.</summary>
    private void UpdateMusic(double dt)
    {
        if (_audio is null) return;
        _audio.MusicVolume = _settings.MusicVolume;
        _audio.EffectsVolume = _settings.EffectsVolume;
        _audio.Update(dt);
        if (_conn.Welcome is not { } w) { _audio.Music("town_act1"); return; }
        if (Now < _bossMusicUntil) { _audio.Music("boss"); return; }
        var zone = _data.Zones.GetValueOrDefault(w.ZoneId);
        string track = zone?.Music is { Length: > 0 } m ? m : zone?.Prologue == true ? "prologue" : w.IsTown ? $"town_act{Math.Max(1, w.Act)}" : $"act{Math.Max(1, w.Act)}_field";
        _audio.Music(track);
        Ambience(w, zone);
    }

    /// <summary>The air of the place (wind, rain, a crypt's drip, a camp's fires), plus crackling when you stand near a fire.</summary>
    private void Ambience(WelcomeInfo w, ZoneDef? zone)
    {
        var a = zone?.Ambience;
        string bed = a?.Sound is { Length: > 0 } snd ? snd : w.IsTown ? "town" : "wind";
        _audio!.Loop("amb_" + bed, 0.7f);
        if (zone is null || !_conn.Entities.TryGetValue(_conn.YourId, out var me)) return;
        // Close to death, your heartbeat comes up under everything.
        float hp = me.Current.MaxHp > 0 ? me.Current.Hp / (float)me.Current.MaxHp : 1;
        if (!me.Current.Dead && hp < 0.3f) _audio.Loop("amb_heartbeat", (0.3f - hp) / 0.3f * 0.9f + 0.25f);
        float near = float.MaxValue;
        var at = me.Current.Pos;
        foreach (var d in zone.Decor)
            if (_data.Props.TryGetValue(d.Prop, out var pd) && pd.Light is { Flame: true })
                near = MathF.Min(near, Vec2.Distance(at, new Vec2(d.X, d.Y)));
        foreach (var l in zone.Lights)
            if (l.Light.Flame) near = MathF.Min(near, Vec2.Distance(at, new Vec2(l.X, l.Y)));
        if (near < 520 && bed is not ("fire" or "camp")) _audio.Loop("amb_fire", (1 - near / 520) * 0.8f);
    }

    private int _stepIndex;

    /// <summary>Your own footsteps, quietly, on the beat of the walk.</summary>
    private void StepSound() => Sfx($"step_{_stepIndex++ % 4 + 1}", 0.22f);
}
