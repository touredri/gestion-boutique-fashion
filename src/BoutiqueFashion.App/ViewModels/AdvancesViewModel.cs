using System.Collections.ObjectModel;
using System.Globalization;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoutiqueFashion.App.ViewModels;

/// <summary>
/// Encaissement des tranches d'avance, au comptoir.
///
/// Ces versements vivaient dans l'écran Clients, désormais réservé au gérant — or c'est le vendeur
/// qui reçoit le client venu payer sa tranche. Un écran dédié règle la contradiction sans lui
/// rouvrir la fiche client complète, avec ses plafonds, ses marges et son historique.
/// </summary>
public partial class AdvancesViewModel(ICreditService credits, ManagerSession session) : ObservableObject, ILoadable
{
    public ManagerSession Session => session;
    public ObservableCollection<CreditSummary> Items { get; } = [];
    public ObservableCollection<CreditPaymentRow> Payments { get; } = [];
    /// <summary>Le crédit ne peut évidemment pas servir à rembourser un crédit.</summary>
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = [.. Enum.GetValues<PaymentMode>().Where(x => x != PaymentMode.Credit)];

    [ObservableProperty] private CreditSummary? selected;
    [ObservableProperty] private string amount = string.Empty;
    [ObservableProperty] private PaymentMode mode = PaymentMode.Cash;
    [ObservableProperty] private string reference = string.Empty;
    [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private bool showSettled;
    [ObservableProperty] private bool isBusy;

    partial void OnShowSettledChanged(bool value) => _ = LoadAsync();

    partial void OnSelectedChanged(CreditSummary? value)
    {
        Payments.Clear();
        Amount = string.Empty;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SettlesAdvance));
        if (value is not null) _ = LoadPaymentsAsync(value.Id);
    }

    partial void OnAmountChanged(string value) => OnPropertyChanged(nameof(SettlesAdvance));

    public bool HasSelection => Selected is not null;

    /// <summary>Ce versement solde-t-il l'avance ? S'il s'agit d'une réservation, c'est le moment
    /// où la marchandise change de mains : il faut le dire avant de valider.</summary>
    public bool SettlesAdvance =>
        Selected is { IsReserved: true }
        && long.TryParse(Amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= Selected.BalanceXof;

    public async Task LoadAsync()
    {
        var keep = Selected?.Id;
        Items.Clear();
        foreach (var row in await credits.ListAsync())
        {
            var settled = row.Status is CreditStatus.Paid or CreditStatus.Cancelled;
            if (settled && !ShowSettled) continue;
            Items.Add(row);
        }
        Selected = keep is Guid id ? Items.FirstOrDefault(x => x.Id == id) : null;
    }

    private async Task LoadPaymentsAsync(Guid creditId)
    {
        try
        {
            Payments.Clear();
            foreach (var row in await credits.ListPaymentsAsync(creditId)) Payments.Add(row);
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task Refresh() => await LoadAsync();

    /// <summary>Le rechargement suit, via OnShowSettledChanged.</summary>
    [RelayCommand] private void ToggleSettled() => ShowSettled = !ShowSettled;

    [RelayCommand] private void PayInFull()
    {
        if (Selected is null) return;
        Amount = Selected.BalanceXof.ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private async Task Pay()
    {
        if (Selected is null) { Status = "Sélectionnez d'abord une avance."; return; }
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!long.TryParse(Amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                Status = "Saisissez le montant reçu.";
                return;
            }
            if (value > Selected.BalanceXof)
            {
                Status = $"Le versement dépasse le solde restant ({Selected.BalanceXof:N0} FCFA).";
                return;
            }

            var handover = Selected.IsReserved && value == Selected.BalanceXof;
            var question = handover
                ? $"Encaisser {value:N0} FCFA et remettre la marchandise à {Selected.CustomerName} ? L'avance sera soldée."
                : $"Encaisser {value:N0} FCFA pour {Selected.CustomerName} ?";
            if (!UiConfirm.Ask(question)) return;

            var result = await credits.PayAsync(Selected.Id, value, Mode, NullIfEmpty(Reference));
            Status = result.NewBalanceXof == 0
                ? $"Reçu {result.Number} · avance soldée."
                : $"Reçu {result.Number} · reste {result.NewBalanceXof:N0} FCFA.";
            UiFeedback.Success(handover ? $"{Status}\nRemettez la marchandise au client." : Status);
            Amount = Reference = string.Empty;
            await LoadAsync();
            if (Selected is not null) await LoadPaymentsAsync(Selected.Id);
        }
        catch (Exception e)
        {
            Status = e.Message;
            UiFeedback.Error($"Versement impossible : {e.Message}");
        }
        finally { IsBusy = false; }
    }

    /// <summary>Contre-passation : correction d'une erreur de saisie, donc réservée au gérant.</summary>
    [RelayCommand]
    private async Task Reverse(CreditPaymentRow? row)
    {
        if (row is null || Selected is null) return;
        if (!session.IsManager) { Status = "Le mode gérant est requis pour contre-passer un versement."; return; }
        if (!UiConfirm.Ask($"Contre-passer le versement {row.Number} de {row.AmountXof:N0} FCFA ? Une contre-écriture tracée sera créée.")) return;
        try
        {
            var result = await credits.ReverseAsync(row.Id, "Erreur de saisie", session.Pin);
            Status = $"Contre-écriture {result.Number} · solde {result.NewBalanceXof:N0} FCFA.";
            await LoadAsync();
            if (Selected is not null) await LoadPaymentsAsync(Selected.Id);
        }
        catch (Exception e) { Status = e.Message; }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
