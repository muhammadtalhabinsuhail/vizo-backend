namespace vizo_backend.Services;

/// <summary>
/// The order lifecycle, and who is allowed to move it.
///
/// ─────────────────────────── THE CHAIN ─────────────────────────────────────
///
///   1  DRAFT                    sales writes it
///   2  SUBMITTED                sales sends it in           -> admin must decide
///   3  CONFIRMED                admin says yes              (or DECLINED)
///   4  INVOICED                 sales OR admin bills it
///   5  TO_ORDER_DEPT            warehouse keeper has the stock moving
///   6  AT_ORDER_DEPT            order dept has it in hand
///   7  PACKAGING                order dept is packing it
///   8  DISPATCHED               order dept sends it out
///   9  DELIVERED                sales confirms it arrived
///
/// ─────────────────────────── AND WHO MOVES IT ──────────────────────────────
///
/// One rule above all the others: THE SUPER ADMIN CAN SET ANY STATUS, in any
/// direction, at any time. Dispatched back to Submitted is allowed. Everybody
/// else may only make the one move that is theirs, from the step before it.
///
/// This lives in one class rather than being scattered through the controller
/// because the same rules answer three different questions -- may this person
/// make this move, what is the next move for the button, and what should the
/// dropdown offer -- and three copies of a rule is three rules.
/// </summary>
public static class OrderWorkflow
{
    // ── the statuses, as keys ──────────────────────────────────────────────
    public const string Draft        = "DRAFT";
    public const string Submitted    = "SUBMITTED";
    public const string Confirmed    = "CONFIRMED";
    public const string Declined     = "DECLINED";
    public const string Invoiced     = "INVOICED";
    public const string ToOrderDept  = "TO_ORDER_DEPT";
    public const string AtOrderDept  = "AT_ORDER_DEPT";
    public const string Packaging    = "PACKAGING";
    public const string Dispatched   = "DISPATCHED";
    public const string Delivered    = "DELIVERED";

    public const string CreditHold   = "CREDIT_HOLD";
    public const string Cancelled    = "CANCELLED";
    public const string Returned     = "RETURNED";

    public const string RoleAdmin     = "super-admin";
    public const string RoleSales     = "sales";
    public const string RoleOrderDept = "order-dept";
    public const string RoleWarehouse = "warehouse-keeper";
    public const string RoleAccountant = "accountant";

    /// <summary>The chain, in order. Step number is index + 1.</summary>
    public static readonly IReadOnlyList<string> Chain = new[]
    {
        Draft, Submitted, Confirmed, Invoiced,
        ToOrderDept, AtOrderDept, Packaging, Dispatched, Delivered
    };

    /// <summary>Where a status sits in the chain, or null if it is off it.</summary>
    public static int? Step(string statusKey)
    {
        var i = Chain.ToList().IndexOf(statusKey);
        return i < 0 ? null : i + 1;
    }

    /// <summary>The status that normally comes next, or null at the end.</summary>
    public static string? NextInChain(string statusKey)
    {
        var i = Chain.ToList().IndexOf(statusKey);
        if (i < 0 || i == Chain.Count - 1) return null;
        return Chain[i + 1];
    }

    /// <summary>
    /// Every move anybody other than the Super Admin is allowed to make.
    ///
    /// Read it as: from THIS status, to THAT status, these roles may do it.
    /// A move not in this table is refused for everyone except the admin.
    /// </summary>
    private static readonly List<(string From, string To, string[] Roles)> Moves = new()
    {
        // Sales writes the order and sends it in.
        (Draft,       Submitted,   new[] { RoleSales, RoleOrderDept }),

        // Only the admin decides whether it goes ahead.
        (Submitted,   Confirmed,   new[] { RoleAdmin }),
        (Submitted,   Declined,    new[] { RoleAdmin }),

        // Billing it. The brief is explicit: sales OR admin, once confirmed.
        (Confirmed,   Invoiced,    new[] { RoleSales, RoleAdmin }),

        // The warehouse keeper picks the stock and starts it moving. Allowed
        // from either step: an order can be sent to the floor before the
        // invoice is raised, and often is.
        (Confirmed,   ToOrderDept, new[] { RoleWarehouse }),
        (Invoiced,    ToOrderDept, new[] { RoleWarehouse }),

        // The order department takes it from there.
        (ToOrderDept, AtOrderDept, new[] { RoleOrderDept }),
        (AtOrderDept, Packaging,   new[] { RoleOrderDept }),
        (Packaging,   Dispatched,  new[] { RoleOrderDept }),

        // Sales confirms the customer actually got it -- they are the one who
        // will hear about it if the customer did not.
        (Dispatched,  Delivered,   new[] { RoleSales }),
    };

    /// <summary>
    /// May this role move an order from one status to another?
    ///
    /// The Super Admin may always. That is the point of the role, and the brief
    /// asks for it in those words: any status, forward or backward.
    /// </summary>
    public static bool CanMove(string roleKey, string from, string to)
    {
        if (roleKey == RoleAdmin) return true;
        if (from == to) return false;

        return Moves.Any(m => m.From == from && m.To == to && m.Roles.Contains(roleKey));
    }

    /// <summary>
    /// The single move this person can make on an order in this state, if there
    /// is exactly one. Drives the one-click button next to Print Bill.
    ///
    /// For the Super Admin that is the next step in the chain -- they can set
    /// anything, but the button should offer the obvious thing.
    /// </summary>
    public static string? NextFor(string roleKey, string currentStatus)
    {
        if (roleKey == RoleAdmin)
        {
            /* A declined or cancelled order has no natural "next"; the admin
               uses the dropdown to put it wherever they want. */
            if (currentStatus is Declined or Cancelled or Returned) return null;
            return NextInChain(currentStatus);
        }

        var mine = Moves
            .Where(m => m.From == currentStatus && m.Roles.Contains(roleKey))
            /* Declining is never the one-click action. It needs a reason and a
               moment's thought, so it stays a deliberate choice. */
            .Where(m => m.To != Declined)
            .Select(m => m.To)
            .ToList();

        return mine.Count == 1 ? mine[0] : null;
    }

    /// <summary>
    /// Everything this person may set the order to right now.
    ///
    /// The admin gets the whole list, which is what makes the dropdown a
    /// dropdown rather than a single button.
    /// </summary>
    public static IReadOnlyList<string> AllowedTargets(string roleKey, string currentStatus)
    {
        if (roleKey == RoleAdmin)
        {
            return Chain
                .Concat(new[] { Declined, CreditHold, Cancelled })
                .Where(s => s != currentStatus)
                .ToList();
        }

        return Moves
            .Where(m => m.From == currentStatus && m.Roles.Contains(roleKey))
            .Select(m => m.To)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// A salesperson may only create an order as a draft or submit it. Anything
    /// further is somebody else's decision to make.
    /// </summary>
    public static bool CanCreateWithStatus(string roleKey, string statusKey) =>
        roleKey == RoleAdmin || statusKey is Draft or Submitted;

    /// <summary>
    /// Which roles hear about an order arriving at this status, and in what
    /// words. Empty audience means nobody needs telling.
    /// </summary>
    public static (string Kind, string[] Roles, string Purpose, string Body) Announcement(
        string statusKey, string orderNo, string customer, string actor)
        => statusKey switch
        {
            Submitted => (NotificationKinds.OrderCreated,
                new[] { RoleAdmin },
                $"Order submitted by {actor}",
                $"{orderNo} -- {customer}. Waiting for you to confirm or decline it."),

            Confirmed => (NotificationKinds.OrderConfirmed,
                /* The warehouse keeper is the one who acts next, so they are
                   the real audience here -- not just an observer. */
                new[] { RoleAdmin, RoleWarehouse, RoleSales },
                $"Order confirmed by {actor}",
                $"{orderNo} -- {customer}. Warehouse can prepare the stock."),

            Declined => (NotificationKinds.OrderConfirmed,
                new[] { RoleAdmin, RoleSales },
                $"Order declined by {actor}",
                $"{orderNo} -- {customer} was declined."),

            Invoiced => (NotificationKinds.InvoiceRaised,
                new[] { RoleAdmin, RoleAccountant },
                $"Order invoiced by {actor}",
                $"{orderNo} -- {customer} has been invoiced."),

            ToOrderDept => (NotificationKinds.TransferSent,
                new[] { RoleAdmin, RoleOrderDept },
                $"Stock sent by {actor}",
                $"{orderNo} -- {customer}. Stock is on its way to the order department."),

            AtOrderDept => (NotificationKinds.TransferReceived,
                new[] { RoleAdmin, RoleWarehouse },
                $"Stock received by {actor}",
                $"{orderNo} -- {customer}. The order department has the stock."),

            Packaging => (NotificationKinds.OrderPacked,
                new[] { RoleAdmin, RoleSales },
                $"Packing started by {actor}",
                $"{orderNo} -- {customer} is being packed."),

            Dispatched => (NotificationKinds.OrderDispatched,
                new[] { RoleAdmin, RoleSales, RoleAccountant },
                $"Order dispatched by {actor}",
                $"{orderNo} -- {customer} has left."),

            Delivered => (NotificationKinds.OrderDelivered,
                new[] { RoleAdmin, RoleAccountant },
                $"Order delivered",
                $"{orderNo} reached {customer}."),

            _ => ("", Array.Empty<string>(), "", "")
        };
}
