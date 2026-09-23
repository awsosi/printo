namespace Printo.Agent.Printing;

/// <summary>Which page of a job a printer is sent first.</summary>
/// <remarks>
/// A job split across two printers ends up on two stacks, and each stack has to read in the
/// document's order when somebody picks it up. What "first" means depends on how the printer
/// delivers: a laser drops pages face down, so sending page 1 first leaves it at the bottom of
/// the stack face down and on top once turned over - document order. A thermal printer pushes
/// labels out on a continuous strip, and the one printed first is the one furthest from the tear
/// bar; torn off and held, the strip reads last label first. Sending the labels last-first puts
/// the first label at the torn end, where the hand is, so it reads in document order too.
/// </remarks>
public enum PageOrder
{
    /// <summary>The fleet's default for the printer's role.</summary>
    Auto,

    /// <summary>Page 1 first. Right for a face-down laser.</summary>
    FirstPageFirst,

    /// <summary>The last page first. Right for a thermal strip torn off after the job.</summary>
    LastPageFirst,
}

/// <summary>Resolves and applies <see cref="PageOrder"/>.</summary>
public static class PageOrders
{
    /// <summary>What the product does when nobody said: lasers in order, thermal strips reversed.</summary>
    public static PageOrder ProductDefault(bool thermal) =>
        thermal ? PageOrder.LastPageFirst : PageOrder.FirstPageFirst;

    /// <summary>The order in force for one printer: its own, else the fleet's, else the product's.</summary>
    public static PageOrder Resolve(PageOrder printer, bool thermal, PageOrder? fleetDefault)
    {
        if (printer != PageOrder.Auto)
        {
            return printer;
        }

        return fleetDefault is { } fleet and not PageOrder.Auto ? fleet : ProductDefault(thermal);
    }

    /// <summary>Puts pages given in document order into the order they are sent.</summary>
    public static IReadOnlyList<T> Arrange<T>(IEnumerable<T> inDocumentOrder, PageOrder order)
    {
        ArgumentNullException.ThrowIfNull(inDocumentOrder);
        var pages = inDocumentOrder.ToList();
        if (order == PageOrder.LastPageFirst)
        {
            pages.Reverse();
        }

        return pages;
    }
}
