using System.Collections.ObjectModel;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoutiqueFashion.App.ViewModels;

/// <summary>
/// Commandes reçues du site vitrine. Visible du vendeur : c'est lui qui rappelle la cliente et
/// qui encaisse.
///
/// L'écran ne propose pas de cocher « traitée ». Encaisser ouvre l'écran de vente avec le panier
/// déjà rempli, et c'est l'enregistrement de la vente qui fait basculer la commande. Une case à
/// cocher laisserait croire qu'un article est vendu alors qu'il est encore en rayon.
/// </summary>
public partial class OrdersViewModel(IOrderService orders, SyncAgent agent) : ObservableObject, ILoadable
{
    public ObservableCollection<OrderRow> Items { get; } = [];
    [ObservableProperty] private OrderRow? selected;
    [ObservableProperty] private bool showClosed;
    [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private bool isBusy;

    partial void OnShowClosedChanged(bool value) => _ = LoadAsync();
    partial void OnSelectedChanged(OrderRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanCollect));
        OnPropertyChanged(nameof(CanDeliver));
    }

    public bool HasSelection => Selected is not null;
    public bool CanCollect => Selected?.Status == OrderStatus.Pending;
    public bool CanDeliver => Selected?.Status == OrderStatus.Processed;

    /// <summary>Nombre de commandes en attente : sert de pastille dans la navigation.</summary>
    public int PendingCount => Items.Count(x => x.Status == OrderStatus.Pending);
    public bool HasPending => PendingCount > 0;

    public async Task LoadAsync()
    {
        try
        {
            var keep = Selected?.Id;
            var rows = await orders.ListAsync(ShowClosed);
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
            Selected = keep is Guid id ? Items.FirstOrDefault(x => x.Id == id) : null;
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(HasPending));

            if (!agent.IsEnrolled)
                Status = "Ce terminal n'est pas rattaché à une boutique : les commandes du site n'y arrivent pas encore.";
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task Refresh()
    {
        // Une synchronisation forcée avant de recharger : la vendeuse au comptoir ne doit pas
        // attendre le prochain cycle pour voir une commande qu'on vient de lui annoncer.
        await agent.RunAsync();
        await LoadAsync();
    }

    [RelayCommand] private void ToggleClosed() => ShowClosed = !ShowClosed;

    /// <summary>Ouvre la vente avec le panier de la commande. Le basculement d'état viendra de
    /// l'enregistrement de cette vente, pas de ce geste.</summary>
    [RelayCommand]
    private async Task Collect()
    {
        if (Selected is null || AppNavigator.LoadOrderIntoSale is null) return;
        var order = Selected;
        try
        {
            var missing = AppNavigator.LoadOrderIntoSale(order);
            if (missing.Count > 0)
            {
                // Un article disparu du catalogue depuis la commande : le dire plutôt que de
                // laisser un panier incomplet passer inaperçu.
                Status = $"Articles introuvables au catalogue : {string.Join(", ", missing)}. Complétez la vente à la main.";
                UiFeedback.Error(Status);
            }
            if (AppNavigator.Go is not null) await AppNavigator.Go("Sale");
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand]
    private async Task Deliver()
    {
        if (Selected is null || IsBusy) return;
        if (!UiConfirm.Ask($"Confirmer la remise de la commande {Selected.Number} à {Selected.CustomerName} ?")) return;
        IsBusy = true;
        try
        {
            await orders.MarkDeliveredAsync(Selected.Id);
            Status = $"Commande {Selected.Number} remise au client.";
            UiFeedback.Success(Status);
            await LoadAsync();
            await agent.RefreshAsync();
        }
        catch (Exception e) { Status = e.Message; UiFeedback.Error(e.Message); }
        finally { IsBusy = false; }
    }
}
