using System.Collections.ObjectModel;
using System.Globalization;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoutiqueFashion.App.ViewModels;

/// <summary>
/// Écran de caisse. L'ouverture et la clôture vivaient dans une carte repliée du tableau de bord,
/// sans jamais montrer ce que le tiroir devrait contenir : le vendeur comptait à l'aveugle et
/// découvrait l'écart au moment de valider.
///
/// Ici la vacation est nommée, protégée par son propre code, et le montant attendu est affiché
/// en permanence avec son détail.
/// </summary>
public partial class CashViewModel(ICashSessionService cash, IReportService reports, IAppSettingsService settings, ManagerSession session, ShiftSession shift) : ObservableObject, ILoadable
{
    public ManagerSession Session => session;
    public ObservableCollection<ReportRow> CollectedByMode { get; } = [];
    public ObservableCollection<CashClosingRow> Closings { get; } = [];

    [ObservableProperty] private CashDeskState? state;
    [ObservableProperty] private bool isCashOpen;
    [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private bool isBusy;

    // --- Ouverture ---
    [ObservableProperty] private string openingFloat = "0";
    [ObservableProperty] private string operatorName = string.Empty;
    [ObservableProperty] private string shiftPin = string.Empty;
    [ObservableProperty] private string shiftPinConfirm = string.Empty;

    // --- Clôture ---
    [ObservableProperty] private string countedCash = string.Empty;
    [ObservableProperty] private string differenceReason = string.Empty;
    [ObservableProperty] private string closingPin = string.Empty;

    // --- Entrées et sorties d'espèces hors vente ---
    [ObservableProperty] private string movementAmount = string.Empty;
    [ObservableProperty] private string movementReason = string.Empty;
    [ObservableProperty] private string movementPin = string.Empty;
    public ObservableCollection<CashMovementRow> Movements { get; } = [];

    partial void OnCountedCashChanged(string value) => RaiseDifference();
    partial void OnStateChanged(CashDeskState? value) => RaiseDifference();

    private void RaiseDifference()
    {
        OnPropertyChanged(nameof(DifferenceXof));
        OnPropertyChanged(nameof(HasCounted));
        OnPropertyChanged(nameof(DifferenceLabel));
        OnPropertyChanged(nameof(IsShort));
    }

    /// <summary>Écart annoncé avant validation : le vendeur voit ce qu'on va lui reprocher
    /// pendant qu'il peut encore recompter.</summary>
    public long DifferenceXof =>
        State is not null && long.TryParse(CountedCash, NumberStyles.Integer, CultureInfo.InvariantCulture, out var counted)
            ? counted - State.ExpectedCashXof
            : 0;

    public bool HasCounted => !string.IsNullOrWhiteSpace(CountedCash);
    public bool IsShort => DifferenceXof < 0;
    public string DifferenceLabel => DifferenceXof switch
    {
        > 0 => $"Excédent de {DifferenceXof:N0} FCFA",
        < 0 => $"Manque {Math.Abs(DifferenceXof):N0} FCFA",
        _ => "Comptage juste",
    };

    public async Task LoadAsync()
    {
        State = await cash.GetStateAsync();
        IsCashOpen = State is not null;

        CollectedByMode.Clear();
        Movements.Clear();
        if (State is not null)
        {
            foreach (var row in State.CollectedByMode) CollectedByMode.Add(row);
            foreach (var row in State.Movements ?? []) Movements.Add(row);
        }

        // Proposition par défaut : la boutique tient sa propre caisse tant qu'on ne nomme personne.
        if (string.IsNullOrWhiteSpace(OperatorName))
            OperatorName = await settings.GetAsync("Shop.Name") ?? string.Empty;

        Closings.Clear();
        var to = DateTimeOffset.Now.AddDays(1);
        foreach (var row in await reports.CashClosingsAsync(to.AddDays(-31), to)) Closings.Add(row);

        // Ouvrir une vacation avec code arme le verrouillage ; la clôturer le désarme.
        await shift.RefreshAsync();
    }

    [RelayCommand]
    private async Task Open()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!long.TryParse(OpeningFloat, NumberStyles.Integer, CultureInfo.InvariantCulture, out var floatXof) || floatXof < 0)
            {
                Status = "Indiquez un fond de caisse valide.";
                return;
            }
            var pin = NullIfEmpty(ShiftPin);
            if (pin is not null && pin != NullIfEmpty(ShiftPinConfirm))
            {
                Status = "Les deux codes de vacation saisis ne sont pas identiques.";
                return;
            }

            var opened = await cash.OpenAsync(floatXof, NullIfEmpty(OperatorName), pin);
            Status = pin is null
                ? $"Caisse {opened.Number} ouverte au nom de {opened.OperatorName}. Sans code de vacation, seul le code gérant pourra la clôturer."
                : $"Caisse {opened.Number} ouverte au nom de {opened.OperatorName}.";
            ShiftPin = ShiftPinConfirm = string.Empty;
            OpeningFloat = "0";
            UiFeedback.Success(Status);
            await LoadAsync();
        }
        catch (Exception e)
        {
            Status = e.Message;
            UiFeedback.Error($"Ouverture impossible : {e.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task Close()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!long.TryParse(CountedCash, NumberStyles.Integer, CultureInfo.InvariantCulture, out var counted) || counted < 0)
            {
                Status = "Saisissez le montant réel des espèces comptées.";
                return;
            }
            var summary = DifferenceXof == 0
                ? $"Clôturer la caisse avec {counted:N0} FCFA comptés ?"
                : $"Clôturer la caisse avec {counted:N0} FCFA comptés ? {DifferenceLabel}.";
            if (!UiConfirm.Ask(summary)) return;

            // En mode gérant, le code a déjà été saisi pour ouvrir la session : le redemander
            // n'ajouterait aucune sécurité et ferait retaper un code à chaque clôture.
            var pin = NullIfEmpty(ClosingPin) ?? (session.IsManager ? NullIfEmpty(session.Pin) : null);
            var closed = await cash.CloseAsync(counted, NullIfEmpty(DifferenceReason), pin);

            Status = closed.DifferenceXof == 0
                ? $"Caisse {closed.Number} clôturée sans écart."
                : $"Caisse {closed.Number} clôturée · écart de {closed.DifferenceXof:N0} FCFA.";
            CountedCash = DifferenceReason = ClosingPin = string.Empty;
            UiFeedback.Success(Status);
            await LoadAsync();
        }
        catch (Exception e)
        {
            Status = e.Message;
            UiFeedback.Error($"Clôture impossible : {e.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void UseReason(string reason) => MovementReason = reason;

    [RelayCommand] private Task RecordIn() => RecordMovementAsync(CashMovementDirection.In);
    [RelayCommand] private Task RecordOut() => RecordMovementAsync(CashMovementDirection.Out);

    private async Task RecordMovementAsync(CashMovementDirection direction)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!long.TryParse(MovementAmount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            {
                Status = "Saisissez le montant du mouvement.";
                return;
            }
            if (string.IsNullOrWhiteSpace(MovementReason))
            {
                Status = "Indiquez le motif : sans lui, le mouvement sera indiscernable d'un écart de caisse.";
                return;
            }

            var label = direction == CashMovementDirection.In ? "Faire entrer" : "Sortir";
            if (!UiConfirm.Ask($"{label} {amount:N0} FCFA du tiroir ? Motif : {MovementReason.Trim()}")) return;

            // Le code gérant déjà saisi vaut autorisation pour un dépassement de plafond.
            var pin = NullIfEmpty(MovementPin) ?? (session.IsManager ? NullIfEmpty(session.Pin) : null);
            await cash.RecordMovementAsync(direction, amount, MovementReason.Trim(), pin);

            Status = $"{Libelles.Text(direction)} de {amount:N0} FCFA enregistrée.";
            MovementAmount = MovementReason = MovementPin = string.Empty;
            UiFeedback.Success(Status);
            await LoadAsync();
        }
        catch (Exception e)
        {
            Status = e.Message;
            UiFeedback.Error($"Mouvement refusé : {e.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private async Task Refresh() => await LoadAsync();

    [RelayCommand] private async Task GoToSale() { if (AppNavigator.Go is not null) await AppNavigator.Go("Sale"); }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
