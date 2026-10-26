using NdeipiChat.SubApps.Farm.Models;

namespace NdeipiChat.SubApps.Farm.Services;

public record GameResult(bool Ok, string Message);

public class AnimalRegistration
{
    public Species Species { get; set; } = Species.Cattle;
    public string Name { get; set; } = "";
    public string TagNumber { get; set; } = "";
    public string Breed { get; set; } = "";
    public double WeightKg { get; set; } = 350;
}

/// <summary>Game engine: owns the farm state, the day clock and every player action.</summary>
public sealed class FarmGameService : IDisposable
{
    public static readonly TimeSpan DayLength = TimeSpan.FromSeconds(20);

    private readonly INodeBossClient _node;
    private readonly FarmStorage _storage;
    private readonly Random _rng = new();
    private Timer? _timer;
    private bool _ticking;

    public FarmGameService(INodeBossClient node, FarmStorage storage)
    {
        _node = node;
        _storage = storage;
    }

    public FarmState State { get; private set; } = FarmState.CreateNew();
    public bool Initialized { get; private set; }
    public bool AutoPlay { get; private set; } = true;
    public bool Busy => _ticking;
    public GameResult? Toast { get; private set; }
    public int ToastId { get; private set; }

    public event Action? Changed;

    // ───────────────────────── lifecycle ─────────────────────────

    public async Task InitAsync()
    {
        if (Initialized) return;
        var saved = await _storage.LoadAsync();
        if (saved is not null && saved.Tiles.Count == FarmState.GridSize * FarmState.GridSize)
        {
            State = saved;
        }
        else
        {
            SousveillanceLedger.Append(State, "GENESIS", $"Farm '{State.FarmName}' opened on NodeBoss");
            Log("🌍", "Welcome! Cut forage, feed the kraal and register your first cow.");
        }

        _timer = new Timer(_ => { if (AutoPlay) _ = AdvanceDayAsync(); }, null, DayLength, DayLength);
        Initialized = true;
        Notify();
    }

    public void ToggleAutoPlay()
    {
        AutoPlay = !AutoPlay;
        Notify();
    }

    public async Task ResetAsync()
    {
        State = FarmState.CreateNew();
        SousveillanceLedger.Append(State, "GENESIS", $"Farm '{State.FarmName}' opened on NodeBoss");
        Log("🌍", "Fresh farm. Cut forage, feed the kraal and register your first cow.");
        await _storage.ClearAsync();
        Done("Farm reset.");
    }

    public void Dispose() => _timer?.Dispose();

    // ───────────────────────── queries ─────────────────────────

    public Tile GetTile(int index) => State.Tiles[index];
    public IEnumerable<Animal> AnimalsIn(int kraalIndex) => State.Animals.Where(a => a.KraalIndex == kraalIndex);
    public int NodeCount => State.Tiles.Count(t => t.Kind == TileKind.NodeBoss);

    public double DailyForageNeed(int kraalIndex) =>
        AnimalsIn(kraalIndex).Where(a => a.Status != VerificationStatus.Rejected).Sum(a => Rules.ForageNeed(a.Species));

    public static bool Has(Tile t, RegenPractice p) => t.Practices.ContainsKey(p);

    // ───────────────────────── building ─────────────────────────

    public GameResult Build(int index, TileKind kind)
    {
        var tile = GetTile(index);
        if (kind == TileKind.Empty) return Fail("Pick something to build.");
        if (tile.Kind != TileKind.Empty) return Fail("That plot is already in use.");
        var cost = Rules.BuildCost[kind];
        if (State.Ndx < cost) return Fail($"You need {cost:0} NDX to build a {Rules.Label(kind)}.");

        State.Ndx -= cost;
        tile.Kind = kind;
        tile.Level = 1;
        if (kind == TileKind.Pasture) tile.Forage = 40;

        SousveillanceLedger.Append(State, "BUILD", $"{kind}@plot{index + 1}");
        AddXp(10);
        Log(Rules.Icon(kind), $"Built a {Rules.Label(kind)} on plot {index + 1} (−{cost:0} NDX).");
        return Done($"{Rules.Label(kind)} built.");
    }

    public GameResult Upgrade(int index)
    {
        var t = GetTile(index);
        if (t.Kind == TileKind.Empty) return Fail("Nothing to upgrade.");
        if (t.Level >= Rules.MaxLevel) return Fail("Already at max level.");
        var cost = Rules.UpgradeCost(t);
        if (State.Ndx < cost) return Fail($"Upgrade needs {cost:0} NDX.");

        State.Ndx -= cost;
        t.Level++;
        SousveillanceLedger.Append(State, "UPGRADE", $"{t.Kind}@plot{index + 1}→L{t.Level}");
        AddXp(20);
        Log("⬆️", $"{Rules.Label(t.Kind)} on plot {index + 1} upgraded to level {t.Level}.");
        return Done($"Upgraded to level {t.Level}.");
    }

    public GameResult Demolish(int index)
    {
        var t = GetTile(index);
        if (t.Kind == TileKind.Empty) return Fail("Plot is already empty.");
        if (t.Kind == TileKind.Kraal && AnimalsIn(index).Any()) return Fail("Move the herd out before demolishing this kraal.");
        if (t.Kind == TileKind.NodeBoss && NodeCount == 1 && State.StakedNdx > 0) return Fail("Unstake your NDX before removing your last node.");

        var kind = t.Kind;
        var refund = Rules.BuildCost[kind] * 0.5;
        State.Ndx += refund;
        State.Tiles[index] = new Tile { Index = index };
        SousveillanceLedger.Append(State, "DEMOLISH", $"{kind}@plot{index + 1}");
        Log("🧹", $"Cleared {Rules.Label(kind)} on plot {index + 1} (+{refund:0} NDX salvage).");
        return Done("Plot cleared.");
    }

    // ───────────────────────── livestock ─────────────────────────

    public GameResult RegisterAnimal(int kraalIndex, AnimalRegistration form)
    {
        var kraal = GetTile(kraalIndex);
        if (kraal.Kind != TileKind.Kraal) return Fail("Animals must be registered into a kraal.");
        if (string.IsNullOrWhiteSpace(form.Name) || string.IsNullOrWhiteSpace(form.TagNumber))
            return Fail("Name and ear-tag number are required.");

        var tag = form.TagNumber.Trim().ToUpperInvariant();
        if (State.Animals.Any(a => a.TagNumber == tag)) return Fail($"Ear tag {tag} is already registered.");
        if (AnimalsIn(kraalIndex).Count() >= Rules.KraalCapacity(kraal)) return Fail("This kraal is full — upgrade it or build another.");

        var fee = Rules.RegistrationFee(form.Species);
        if (State.Ndx < fee) return Fail($"Registration costs {fee:0} NDX.");
        State.Ndx -= fee;

        var animal = new Animal
        {
            Species = form.Species,
            Name = form.Name.Trim(),
            TagNumber = tag,
            Breed = string.IsNullOrWhiteSpace(form.Breed) ? "Unknown" : form.Breed.Trim(),
            WeightKg = form.WeightKg,
            KraalIndex = kraalIndex,
            RegisteredDay = State.Day,
        };
        State.Animals.Add(animal);

        SousveillanceLedger.Append(State, "ANIMAL_REGISTERED", $"{animal.Species}:{tag}:{animal.Name}:{animal.WeightKg:0}kg@kraal{kraalIndex + 1}");
        AddXp(15);
        Log(animal.Icon, $"{animal.Name} ({tag}) registered — awaiting NodeBoss attestation tomorrow.");
        return Done($"{animal.Name} registered. Attestation runs at the next day tick.");
    }

    public GameResult HarvestForage(int pastureIndex)
    {
        var t = GetTile(pastureIndex);
        if (t.Kind != TileKind.Pasture) return Fail("Only pastures produce forage.");
        if (t.Forage < 1) return Fail("The grass needs time to regrow.");

        var cut = Math.Floor(t.Forage);
        t.Forage -= cut;
        State.Inventory.ForageKg += cut;
        AddXp(2);
        return Done($"Cut {cut:0} kg of forage.");
    }

    public GameResult FeedKraal(int kraalIndex)
    {
        var k = GetTile(kraalIndex);
        if (k.Kind != TileKind.Kraal) return Fail("Select a kraal to feed.");
        var need = DailyForageNeed(kraalIndex);
        if (need <= 0) return Fail("No animals in this kraal yet.");

        var room = need * 3 - k.Forage; // trough holds three days of feed
        if (room <= 0) return Fail("The trough is already full.");
        var give = Math.Min(room, State.Inventory.ForageKg);
        if (give <= 0) return Fail("No forage in the barn — cut some from a pasture.");

        State.Inventory.ForageKg -= give;
        k.Forage += give;
        AddXp(3);
        return Done($"Filled the trough with {give:0} kg of forage.");
    }

    public GameResult CollectKraal(int kraalIndex)
    {
        var k = GetTile(kraalIndex);
        if (k.Kind != TileKind.Kraal) return Fail("Select a kraal.");
        if (k.ManureKg < 0.5 && k.UrineL < 0.5) return Fail("Nothing to collect yet — fed animals produce daily.");

        var manure = k.ManureKg;
        var urine = k.UrineL;
        State.Inventory.ManureKg += manure;
        State.Inventory.UrineL += urine;
        State.LifetimeManureKg += manure;
        k.ManureKg = 0;
        k.UrineL = 0;

        SousveillanceLedger.Append(State, "BIOMASS_COLLECTED", $"kraal{kraalIndex + 1}:manure={manure:0.0}kg:urine={urine:0.0}L");
        AddXp(5);
        return Done($"Collected {manure:0} kg manure and {urine:0} L urine.");
    }

    // ───────────────────────── processing ─────────────────────────

    public GameResult LoadProcessor(int index)
    {
        var t = GetTile(index);
        var inv = State.Inventory;
        switch (t.Kind)
        {
            case TileKind.CompostPit when inv.ManureKg >= 1:
                t.ManureKg += inv.ManureKg;
                var m = inv.ManureKg;
                inv.ManureKg = 0;
                return Done($"Loaded {m:0} kg manure into the compost pit.");
            case TileKind.UrineTank when inv.UrineL >= 1:
                t.UrineL += inv.UrineL;
                var u = inv.UrineL;
                inv.UrineL = 0;
                return Done($"Loaded {u:0} L urine into the bio-tank.");
            case TileKind.CompostPit:
                return Fail("No manure in the barn — collect it from a kraal.");
            case TileKind.UrineTank:
                return Fail("No urine in the barn — collect it from a kraal.");
            default:
                return Fail("Select a compost pit or urine tank.");
        }
    }

    public GameResult CollectProcessor(int index)
    {
        var t = GetTile(index);
        if (t.Kind == TileKind.CompostPit && t.CompostKg >= 1)
        {
            var c = t.CompostKg;
            State.Inventory.CompostKg += c;
            t.CompostKg = 0;
            AddXp(5);
            return Done($"Collected {c:0} kg of compost.");
        }
        if (t.Kind == TileKind.UrineTank && t.BioFertL >= 1)
        {
            var b = t.BioFertL;
            State.Inventory.BioFertL += b;
            t.BioFertL = 0;
            AddXp(5);
            return Done($"Collected {b:0} L of bio-fertilizer.");
        }
        return Fail("Nothing ready yet — processing happens each day.");
    }

    // ───────────────────────── fields & practices ─────────────────────────

    public GameResult Plant(int index)
    {
        var t = GetTile(index);
        if (t.Kind != TileKind.RegenField) return Fail("Plant on a regen field.");
        if (t.Crop != CropStage.Fallow) return Fail("This field is already planted.");
        if (State.Ndx < Rules.SeedCost) return Fail($"Seed costs {Rules.SeedCost:0} NDX.");

        State.Ndx -= Rules.SeedCost;
        t.Crop = CropStage.Planted;
        t.CropDays = 0;
        AddXp(4);
        return Done("Seeds in the ground. 🌱");
    }

    public GameResult HarvestCrop(int index)
    {
        var t = GetTile(index);
        if (t.Kind != TileKind.RegenField || t.Crop != CropStage.Ready) return Fail("Nothing ready to harvest.");

        var yield = Rules.BaseYieldKg * t.Level * (1 + 0.25 * t.Practices.Count);
        State.Inventory.ProduceKg += yield;
        t.Crop = CropStage.Fallow;
        t.CropDays = 0;
        SousveillanceLedger.Append(State, "HARVEST", $"field{index + 1}:{yield:0}kg:practices={string.Join('+', t.Practices.Keys)}");
        AddXp(12);
        Log("🌽", $"Harvested {yield:0} kg of produce from plot {index + 1}.");
        return Done($"Harvested {yield:0} kg.");
    }

    public GameResult ApplyPractice(int index, RegenPractice practice)
    {
        var t = GetTile(index);
        var info = Rules.Practices[practice];
        if (t.Kind != info.AppliesTo) return Fail($"{info.Label} applies to a {Rules.Label(info.AppliesTo)}.");
        if (Has(t, practice)) return Fail($"{info.Label} is already active here.");

        var inv = State.Inventory;
        if (State.Ndx < info.NdxCost) return Fail($"{info.Label} costs {info.NdxCost:0} NDX.");
        if (inv.CompostKg < info.CompostKg) return Fail($"Needs {info.CompostKg:0} kg compost — make some in a compost pit.");
        if (inv.BioFertL < info.BioFertL) return Fail($"Needs {info.BioFertL:0} L bio-fertilizer — make some in a urine tank.");

        State.Ndx -= info.NdxCost;
        inv.CompostKg -= info.CompostKg;
        inv.BioFertL -= info.BioFertL;
        t.Practices[practice] = info.DurationDays;

        SousveillanceLedger.Append(State, "PRACTICE_STARTED", $"{practice}@plot{index + 1}");
        AddXp(15);
        Log(info.Icon, $"{info.Label} started on plot {index + 1}.");
        return Done($"{info.Label} active — earning carbon daily.");
    }

    // ───────────────────────── NodeBoss: carbon, staking, market ─────────────────────────

    public async Task<GameResult> VerifyAndMintAsync()
    {
        if (NodeCount == 0) return Fail("Build a NodeBoss node first — it verifies and mints credits.");
        if (State.PendingCarbonT < Rules.MinMintT) return Fail($"Need at least {Rules.MinMintT} tCO₂e pending to mint.");

        var evidence = State.Tiles
            .SelectMany(t => t.Practices.Keys.Select(p => $"{p}@plot{t.Index + 1}"))
            .Concat(State.Tiles.Where(t => t.Kind is TileKind.CompostPit or TileKind.UrineTank).Select(t => $"{t.Kind}@plot{t.Index + 1}"))
            .ToList();

        var submitted = State.PendingCarbonT;
        var r = await _node.VerifyCarbonAsync(submitted, evidence, NodeCount, State.Day);
        if (!r.Approved)
        {
            Log("❌", $"Verification rejected: {r.Note}");
            return Fail(r.Note);
        }

        State.PendingCarbonT = 0;
        State.CarbonCredits += r.Quantity;
        State.LifetimeCreditsMinted += r.Quantity;
        SousveillanceLedger.Append(State, "CARBON_MINT",
            $"{r.Quantity:0.000}tCO2e ref={r.Reference} submitted={submitted:0.000} by={r.Attestor}");
        AddXp((int)(r.Quantity * 100) + 20);
        Log("🪙", $"Minted {r.Quantity:0.000} carbon credits ({r.Reference}). {r.Note}");
        return Done($"Minted {r.Quantity:0.000} credits.");
    }

    public GameResult Stake(double amount)
    {
        if (NodeCount == 0) return Fail("Build a NodeBoss node to stake into.");
        amount = Math.Round(amount, 2);
        if (amount <= 0 || amount > State.Ndx) return Fail("Enter an amount up to your NDX balance.");

        State.Ndx -= amount;
        State.StakedNdx += amount;
        SousveillanceLedger.Append(State, "NDX_STAKE", $"{amount:0.00}NDX→{NodeCount}node(s)");
        Log("🔒", $"Staked {amount:0.00} NDX into NodeBoss.");
        return Done($"Staked {amount:0.00} NDX.");
    }

    public GameResult Unstake(double amount)
    {
        amount = Math.Round(amount, 2);
        if (amount <= 0 || amount > State.StakedNdx) return Fail("Enter an amount up to your staked balance.");

        State.StakedNdx -= amount;
        State.Ndx += amount;
        SousveillanceLedger.Append(State, "NDX_UNSTAKE", $"{amount:0.00}NDX");
        return Done($"Unstaked {amount:0.00} NDX.");
    }

    public GameResult SellCredits(double amount)
    {
        amount = Math.Round(amount, 3);
        if (amount <= 0 || amount > State.CarbonCredits + 1e-9) return Fail("Enter an amount up to your credit balance.");

        var proceeds = amount * State.CreditPrice;
        State.CarbonCredits -= amount;
        State.Ndx += proceeds;
        SousveillanceLedger.Append(State, "CARBON_SALE", $"{amount:0.000}tCO2e@{State.CreditPrice:0.00}={proceeds:0.00}NDX");
        Log("💱", $"Sold {amount:0.000} credits for {proceeds:0.00} NDX.");
        return Done($"+{proceeds:0.00} NDX");
    }

    public GameResult SellProduce()
    {
        var kg = State.Inventory.ProduceKg;
        if (kg < 1) return Fail("No produce to sell — harvest a field first.");

        var proceeds = kg * Rules.ProducePrice;
        State.Inventory.ProduceKg = 0;
        State.Ndx += proceeds;
        Log("🧺", $"Sold {kg:0} kg of produce for {proceeds:0.00} NDX.");
        return Done($"+{proceeds:0.00} NDX");
    }

    public GameResult ClaimMission(Mission m)
    {
        if (State.ClaimedMissions.Contains(m.Id)) return Fail("Already claimed.");
        if (!m.IsComplete(State)) return Fail("Mission not complete yet.");

        State.ClaimedMissions.Add(m.Id);
        State.Ndx += m.RewardNdx;
        AddXp(m.RewardXp);
        Log("🎯", $"Mission complete: {m.Title} (+{m.RewardNdx:0} NDX).");
        return Done($"{m.Title}: +{m.RewardNdx:0} NDX, +{m.RewardXp} XP");
    }

    // ───────────────────────── day tick ─────────────────────────

    public async Task AdvanceDayAsync()
    {
        if (_ticking || !Initialized) return;
        _ticking = true;
        try
        {
            var s = State;
            s.Day++;
            s.Weather = RollWeather();
            double carbonToday = 0;

            foreach (var t in s.Tiles)
            {
                switch (t.Kind)
                {
                    case TileKind.Pasture:
                        var regen = Rules.PastureRegen * t.Level * Rules.ForageFactor(s.Weather)
                                    * (Has(t, RegenPractice.RotationalGrazing) ? 1.5 : 1);
                        t.Forage = Math.Min(Rules.PastureCap * t.Level, t.Forage + regen);
                        break;

                    case TileKind.Kraal:
                        RunKraal(t);
                        break;

                    case TileKind.CompostPit:
                        var c = Math.Min(t.ManureKg, Rules.CompostThroughput * t.Level);
                        t.ManureKg -= c;
                        t.CompostKg += c * Rules.CompostYield;
                        s.LifetimeCompostKg += c * Rules.CompostYield;
                        carbonToday += c * Rules.CompostCarbonPerKg;
                        break;

                    case TileKind.UrineTank:
                        var u = Math.Min(t.UrineL, Rules.UrineThroughput * t.Level);
                        t.UrineL -= u;
                        t.BioFertL += u;
                        carbonToday += u * Rules.UrineCarbonPerL;
                        break;

                    case TileKind.RegenField:
                        GrowCrop(t);
                        break;

                    case TileKind.NodeBoss:
                        s.SolarKwh += Rules.NodeKwhPerDay * t.Level * Rules.SolarFactor(s.Weather);
                        break;
                }

                foreach (var (practice, days) in t.Practices.ToList())
                {
                    var perDay = Rules.Practices[practice].CarbonPerDay * t.Level;
                    carbonToday += perDay;
                    t.SoilCarbonT += perDay;
                    if (days == 1) t.Practices.Remove(practice);
                    else if (days > 1) t.Practices[practice] = days - 1;
                }
            }

            s.PendingCarbonT += carbonToday;

            if (s.StakedNdx > 0)
            {
                var rate = await _node.GetDailyStakingRateAsync(NodeCount);
                s.Ndx += s.StakedNdx * rate;
            }

            s.CreditPrice = Math.Clamp(s.CreditPrice * (1 + (_rng.NextDouble() - 0.48) * 0.08), 6, 30);

            foreach (var a in s.Animals.Where(a => a.Status == VerificationStatus.Pending && a.RegisteredDay < s.Day).ToList())
                await AttestAsync(a);

            Log(Rules.WeatherIcon(s.Weather), $"Day {s.Day} — {s.Weather}. +{carbonToday:0.000} tCO₂e pending.");
            Notify();
            await SaveAsync();
        }
        finally
        {
            _ticking = false;
        }
    }

    private void RunKraal(Tile k)
    {
        var herd = AnimalsIn(k.Index).Where(a => a.Status != VerificationStatus.Rejected).ToList();
        if (herd.Count == 0) return;

        var need = herd.Sum(a => Rules.ForageNeed(a.Species));
        var fed = Math.Min(1, k.Forage / need);
        k.Forage = Math.Max(0, k.Forage - need);

        foreach (var a in herd)
        {
            k.ManureKg += Rules.ManurePerDay(a.Species) * fed;
            k.UrineL += Rules.UrinePerDay(a.Species) * fed;
            if (fed >= 0.99)
            {
                a.Health = Math.Min(100, a.Health + 5);
                a.WeightKg += Rules.DailyGainKg(a.Species);
            }
            else
            {
                a.Health = Math.Max(10, a.Health - 15 * (1 - fed));
            }
        }

        if (fed < 0.99) Log("⚠️", $"Kraal {k.Index + 1} trough ran short — herd only {fed:P0} fed.");
    }

    private void GrowCrop(Tile t)
    {
        if (t.Crop is CropStage.Fallow or CropStage.Ready) return;
        t.CropDays++;
        var growDays = Math.Max(2, Rules.CropGrowDays
                                   - (Has(t, RegenPractice.CompostApplication) ? 1 : 0)
                                   - (Has(t, RegenPractice.BioFertilizer) ? 1 : 0));
        if (t.CropDays >= growDays) t.Crop = CropStage.Ready;
        else if (t.CropDays * 2 >= growDays) t.Crop = CropStage.Growing;
    }

    private async Task AttestAsync(Animal a)
    {
        var r = await _node.AttestAnimalAsync(a, State.Day);
        a.Attestor = r.Attestor;
        if (r.Approved)
        {
            a.Status = VerificationStatus.Verified;
            a.AttestationRef = r.Reference;
            if (a.Species == Species.Cattle) a.CattleBondRef = $"CB-{a.TagNumber}-{State.Day:D4}";
            SousveillanceLedger.Append(State, "ANIMAL_ATTESTED", $"{a.TagNumber}:{r.Reference}{(a.CattleBondRef is null ? "" : $":bond={a.CattleBondRef}")}");
            AddXp(20);
            Log("✅", a.Species == Species.Cattle
                ? $"{a.Name} verified — Cattle Bond {a.CattleBondRef} is KandondoCoin-eligible."
                : $"{a.Name} verified on the ledger.");
        }
        else
        {
            a.Status = VerificationStatus.Rejected;
            SousveillanceLedger.Append(State, "ANIMAL_REJECTED", $"{a.TagNumber}:{r.Note}");
            Log("❌", $"{a.Name} ({a.TagNumber}) rejected: {r.Note}");
        }
    }

    private Weather RollWeather() => _rng.NextDouble() switch
    {
        < 0.5 => Weather.Sunny,
        < 0.8 => Weather.Cloudy,
        _ => Weather.Rainy,
    };

    // ───────────────────────── helpers ─────────────────────────

    private void AddXp(int xp)
    {
        var before = State.Level;
        State.Xp += xp;
        if (State.Level > before) Log("⭐", $"Level up! You are now level {State.Level}.");
    }

    private void Log(string icon, string text)
    {
        State.Log.Insert(0, new LogEntry { Day = State.Day, Icon = icon, Text = text });
        if (State.Log.Count > 80) State.Log.RemoveRange(80, State.Log.Count - 80);
    }

    private GameResult Done(string message)
    {
        Toast = new GameResult(true, message);
        ToastId++;
        Notify();
        _ = SaveAsync();
        return Toast;
    }

    private GameResult Fail(string message)
    {
        Toast = new GameResult(false, message);
        ToastId++;
        Notify();
        return Toast;
    }

    private async Task SaveAsync()
    {
        try { await _storage.SaveAsync(State); }
        catch { /* storage unavailable (private mode) — game keeps running in memory */ }
    }

    private void Notify() => Changed?.Invoke();
}
