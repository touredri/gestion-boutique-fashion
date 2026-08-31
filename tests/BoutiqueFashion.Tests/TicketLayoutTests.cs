using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using Xunit;

namespace BoutiqueFashion.Tests;

public class TicketLayoutTests
{
    private static ReceiptData Sample(DocumentStyle style, DocumentType kind = DocumentType.Receipt, bool duplicate = false) =>
        new("MA BOUTIQUE", "Avenue de la Mode, Abidjan", "07 00 00 00 00", "TIC-2026-000001",
            new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero), "Client Exemple",
            [new ReceiptItem("Robe wax élégante - Rouge / M", 2, 15_000, 3_000, 27_000)],
            30_000, 3_000, 27_000,
            [new PaymentDraft(PaymentMode.Cash, 30_000)],
            "Merci de votre visite", duplicate,
            Slogan: "La mode autrement", ReturnPolicy: "Échange ou avoir sous 7 jours",
            ChangeXof: 3_000, Style: style, Kind: kind);

    [Theory]
    [InlineData(DocumentStyle.Classique)]
    [InlineData(DocumentStyle.Moderne)]
    [InlineData(DocumentStyle.Minimal)]
    public void TousStyles_RespectentLaLargeur80Mm(DocumentStyle style)
    {
        foreach (var line in EscPosTicketLayout.Build(Sample(style)))
            Assert.True(line.Text.Length <= 42, $"Ligne trop longue ({line.Text.Length} caractères) : « {line.Text} »");
    }

    [Theory]
    [InlineData(DocumentStyle.Classique)]
    [InlineData(DocumentStyle.Moderne)]
    [InlineData(DocumentStyle.Minimal)]
    public void TousStyles_RespectentLaLargeur58Mm(DocumentStyle style)
    {
        foreach (var line in EscPosTicketLayout.Build(Sample(style), PaperWidth.Mm58))
            Assert.True(line.Text.Length <= 32, $"Ligne trop longue ({line.Text.Length} caractères) : « {line.Text} »");
    }

    [Fact]
    public void Colonnes_CorrespondentALaLargeurDuRouleau()
    {
        Assert.Equal(32, EscPosTicketLayout.Columns(PaperWidth.Mm58));
        Assert.Equal(42, EscPosTicketLayout.Columns(PaperWidth.Mm80));
    }

    [Theory]
    [InlineData(DocumentStyle.Classique)]
    [InlineData(DocumentStyle.Moderne)]
    [InlineData(DocumentStyle.Minimal)]
    public void Avoir_AfficheLeLibelleDuDocument(DocumentStyle style)
    {
        var lines = EscPosTicketLayout.Build(Sample(style, DocumentType.CreditNote));
        Assert.Contains(lines, x => x.Text.Contains("avoir", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicata_AfficheLeMarqueur()
    {
        var lines = EscPosTicketLayout.Build(Sample(DocumentStyle.Classique, duplicate: true));
        Assert.Contains(lines, x => x.Text.Contains("DUPLICATA"));
    }

    [Fact]
    public void TicketClassique_TotalEtPaiementsPresents()
    {
        var lines = EscPosTicketLayout.Build(Sample(DocumentStyle.Classique));
        Assert.Contains(lines, x => x.Text.Contains("TOTAL"));
        Assert.Contains(lines, x => x.Text.Contains("Espèces"));
        Assert.Contains(lines, x => x.Text.Contains("Monnaie rendue"));
    }

    [Fact]
    public void ApercuModele_GenereUnPngValide()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"bf-preview-{Guid.NewGuid():N}");
        try
        {
            var service = new A4DocumentService(new AppPaths(temp));
            var png = service.CreatePreviewImage(Sample(DocumentStyle.Moderne));
            Assert.True(png.Length > 1_000, $"PNG vide ou suspect ({png.Length} octets)");
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}
