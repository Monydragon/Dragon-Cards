using DragonCards.Core;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Media;

namespace DragonCards.Desktop;

internal sealed class AudioService
{
    private readonly Dictionary<string, SoundEffect> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _lastPlayedAt = new(StringComparer.OrdinalIgnoreCase);
    private Func<int> _soundVolume = () => 80;
    private Func<int> _musicVolume = () => 70;
    private Func<bool> _muted = () => false;
    private Func<string, string> _cardType = _ => "";
    private Song? _music;
    private bool _musicStarted;
    private string _musicName = "";
    private double _clock;

    public int LoadedSoundCount => _sounds.Count;
    public int ExpectedSoundCount => SoundKeys.All.Length;
    public bool MusicLoaded => _music is not null;
    public string MusicStatus => _music is null
        ? "BGM not loaded"
        : _muted()
            ? $"{_musicName} muted"
            : $"{_musicName} looping";

    public void Configure(Func<int> soundVolume, Func<int> musicVolume, Func<bool> muted, Func<string, string>? cardType = null)
    {
        _soundVolume = soundVolume;
        _musicVolume = musicVolume;
        _muted = muted;
        _cardType = cardType ?? (_ => "");
    }

    public void Load(ContentManager content)
    {
        foreach (var key in SoundKeys.All)
        {
            TryLoad(content, key);
        }
    }

    public void LoadLoopingMusic(ContentManager content, string assetName, string displayName)
    {
        try
        {
            _music = content.Load<Song>(assetName);
            _musicName = displayName;
            MediaPlayer.IsRepeating = true;
            ApplyMusicSettings();
            StartMusic();
        }
        catch
        {
            _music = null;
            _musicName = "";
            _musicStarted = false;
        }
    }

    public void Update(double elapsedSeconds)
    {
        _clock += elapsedSeconds;
        ApplyMusicSettings();
        if (_music is not null && !_muted() && MediaPlayer.State != MediaState.Playing)
        {
            StartMusic();
        }
    }

    public void Play(string key, double throttleSeconds = 0.03)
    {
        if (_muted() || !_sounds.TryGetValue(key, out var sound))
        {
            return;
        }

        if (_lastPlayedAt.TryGetValue(key, out var last) && _clock - last < throttleSeconds)
        {
            return;
        }

        _lastPlayedAt[key] = _clock;
        sound.Play(Math.Clamp(_soundVolume(), 0, 100) / 100f, 0f, 0f);
    }

    public void RestartMusic()
    {
        if (_music is null)
        {
            return;
        }

        _musicStarted = false;
        StartMusic();
    }

    public void PlayForEvents(IEnumerable<MatchEvent> events)
    {
        foreach (var matchEvent in events)
        {
            Play(SoundForEvent(matchEvent), throttleSeconds: 0.08);
        }
    }

    private void TryLoad(ContentManager content, string key)
    {
        try
        {
            _sounds[key] = content.Load<SoundEffect>($"Audio/{key}");
        }
        catch
        {
            // Audio should never block play; missing assets simply skip playback.
        }
    }

    private void ApplyMusicSettings()
    {
        if (_music is null)
        {
            return;
        }

        try
        {
            MediaPlayer.IsRepeating = true;
            MediaPlayer.Volume = _muted() ? 0f : Math.Clamp(_musicVolume(), 0, 100) / 100f;
            if (_muted() && MediaPlayer.State == MediaState.Playing)
            {
                MediaPlayer.Pause();
            }
        }
        catch
        {
            // Some capture/headless audio devices can reject media calls; gameplay should continue.
        }
    }

    private void StartMusic()
    {
        if (_music is null || _muted())
        {
            return;
        }

        try
        {
            MediaPlayer.IsRepeating = true;
            MediaPlayer.Volume = Math.Clamp(_musicVolume(), 0, 100) / 100f;
            if (!_musicStarted || MediaPlayer.State == MediaState.Stopped)
            {
                MediaPlayer.Play(_music);
                _musicStarted = true;
            }
            else if (MediaPlayer.State == MediaState.Paused)
            {
                MediaPlayer.Resume();
            }
        }
        catch
        {
            _musicStarted = false;
        }
    }

    private string SoundForEvent(MatchEvent matchEvent) => matchEvent.Kind switch
    {
        MatchEventKind.PhaseChanged => SoundKeys.Phase,
        MatchEventKind.CardDrawn => SoundKeys.CardDraw,
        MatchEventKind.CardPlayed => CardPlayedSound(matchEvent.CardId),
        MatchEventKind.CardDiscarded => SoundKeys.CardDiscard,
        MatchEventKind.CardSacrificed => SoundKeys.Sacrifice,
        MatchEventKind.EnergyGained or MatchEventKind.EnergyConverted or MatchEventKind.EnergyRefunded => SoundKeys.EnergyGain,
        MatchEventKind.EnergySpent => SoundKeys.EnergySpend,
        MatchEventKind.CostReduced => SoundKeys.CostReduce,
        MatchEventKind.AttackDeclared => SoundKeys.Attack,
        MatchEventKind.BlockDeclared => SoundKeys.Block,
        MatchEventKind.DamageTaken => SoundKeys.Damage,
        MatchEventKind.TargetChoiceQueued => SoundKeys.TargetPrompt,
        MatchEventKind.TargetResolved => SoundKeys.TargetResolve,
        MatchEventKind.AbilityActivated => SoundKeys.Ability,
        MatchEventKind.CombatActionQueued => SoundKeys.CombatWindow,
        MatchEventKind.CombatResolved => SoundKeys.CombatResolve,
        MatchEventKind.CardReadied => SoundKeys.CardReady,
        MatchEventKind.CardReturnedToHand => SoundKeys.CardReturn,
        _ => SoundKeys.UiClick
    };

    private string CardPlayedSound(string cardId) => _cardType(cardId) switch
    {
        "Unit" => SoundKeys.UnitSummon,
        "Support" => SoundKeys.Support,
        "Spell" => SoundKeys.Spell,
        _ => SoundKeys.CardPlay
    };
}

internal static class SoundKeys
{
    public const string UiClick = "ui_click";
    public const string UiHover = "ui_hover";
    public const string UiBack = "ui_back";
    public const string UiError = "ui_error";
    public const string Phase = "phase";
    public const string CardPlay = "card_play";
    public const string Spell = "spell";
    public const string UnitSummon = "unit_summon";
    public const string Support = "support";
    public const string Ability = "ability";
    public const string Attack = "attack";
    public const string Block = "block";
    public const string Damage = "damage";
    public const string CombatWindow = "combat_window";
    public const string TargetResolve = "target_resolve";
    public const string CardDiscard = "card_discard";
    public const string CardReturn = "card_return";
    public const string CardReady = "card_ready";
    public const string CostReduce = "cost_reduce";
    public const string CardDraw = "card_draw";
    public const string EnergyGain = "energy_gain";
    public const string EnergySpend = "energy_spend";
    public const string Sacrifice = "sacrifice";
    public const string TargetPrompt = "target_prompt";
    public const string Victory = "victory";
    public const string Defeat = "defeat";
    public const string PackOpen = "pack_open";
    public const string RarePull = "rare_pull";
    public const string MythicPull = "mythic_pull";
    public const string CombatResolve = "combat_resolve";

    public static readonly string[] All =
    [
        UiClick,
        UiHover,
        UiBack,
        UiError,
        Phase,
        CardPlay,
        Spell,
        UnitSummon,
        Support,
        Ability,
        Attack,
        Block,
        Damage,
        CombatWindow,
        TargetResolve,
        CardDiscard,
        CardReturn,
        CardReady,
        CostReduce,
        CardDraw,
        EnergyGain,
        EnergySpend,
        Sacrifice,
        TargetPrompt,
        Victory,
        Defeat,
        PackOpen,
        RarePull,
        MythicPull,
        CombatResolve
    ];
}
