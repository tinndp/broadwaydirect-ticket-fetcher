namespace PaciolanEvenue.Core.Models;

/// <summary>One price/ticket-type row from the event page's PL_PT_PRICES array. Units
/// (Price/PerTicketFee/FacilityFee) are UNCONFIRMED - see RECON.md / README.md "Open questions".
/// Kept as raw long, not divided by 100, so no assumption is baked in here.</summary>
public class PriceLevel
{
    public string Pl { get; init; } = "";
    public string PlDesc { get; init; } = "";
    public string Pt { get; init; } = "";
    public string PtDesc { get; init; } = "";
    public long Price { get; init; }
    public long PerTicketFee { get; init; }
    public long FacilityFee { get; init; }
    /// <summary>Per (PL, PT) purchase-quantity rules, verbatim (PLPT_MINQTY / PLPT_MAXQTY /
    /// PLPT_MULTIPLE). Null = not sent.</summary>
    public int? PlptMinQty { get; init; }
    public int? PlptMaxQty { get; init; }
    public int? PlptMultiple { get; init; }
}
