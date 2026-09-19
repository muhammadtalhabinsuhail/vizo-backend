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
///   5  SEEN_BY_WAREHOUSE        warehouse keeper has picked it up
///   6  TO_ORDER_DEPT            warehouse keeper has the stock moving
///   7  AT_ORDER_DEPT            order dept has it in hand
///   8  PACKAGING                order dept is packing it
///   9  DISPATCHED               order dept sends it out
///  10  DELIVERED                sales confirms it arrived
///
/// The warehouse keeper's two steps -- 5 and 6 -- open only once the order is
/// INVOICED, and they are the only two moves that role will ever be offered.
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
    public const string SeenByWarehouse = "SEEN_BY_WAREHOUSE";
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
        Draft, Submitted, Confirmed, Invoiced, SeenByWarehouse,
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

        /* BILLING IT IS THE BACK OFFICE'S JOB, NOT THE REP'S.

           It used to be (Confirmed -> Invoiced, sales or admin). The owner took
           that right off sales: "invoiced/Edit ka status done krna ka right
           sales person se lelo ... wo accountant ya super admin krsakta hai".

           The reasoning behind it is the ordinary one for a distributor: the
           person who negotiated the price should not also be the person who
           settles what the customer is billed. The step is called
           "Invoiced/Edit" on screen because at that moment accounts may still
           correct the order -- see MayEditOrder -- and the invoice is cut from
           whatever the order says once they are done with it. */
        (Confirmed,   Invoiced,    new[] { RoleAccountant }),

        /* The warehouse keeper, and ONLY after the order has been invoiced.
           Two moves, in order: acknowledge it, then send it. That is the whole
           of what this role may do to an order -- AllowedTargets returns these
           and nothing else, and the screen offers nothing else because it asks
           this table rather than deciding for itself.

           Deliberately NOT from CONFIRMED. Picking stock against an order the
           office has not yet billed is how goods leave without an invoice
           behind them. */
        (Invoiced,        SeenByWarehouse, new[] { RoleWarehouse }),
        (SeenByWarehouse, ToOrderDept,     new[] { RoleWarehouse }),

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
    /// May this role cut an invoice for an order at all?
    ///
    /// The same rule as the Confirmed -> Invoiced move above, in a form the
    /// other two routes to a bill can ask: POST /orders/{id}/invoice, and the
    /// "raise the invoice too" tick on a new order. All three used to answer
    /// this question differently -- the chain said sales or admin, the endpoint
    /// said anybody holding invoices.create (which the order desk holds), and
    /// the tick said anybody but sales -- so taking the right off the rep in
    /// one place would simply have moved them to another.
    /// </summary>
    public static bool MayInvoice(string roleKey) =>
        roleKey is RoleAdmin or RoleAccountant;

    /// <summary>
    /// May this role change an order's lines and prices as it stands, with no
    /// approved change request behind them?
    ///
    /// The Super Admin always may. Accounts may while the order is theirs to
    /// deal with -- confirmed, or invoiced and not yet picked -- because that
    /// is the half of "Invoiced/Edit" that is not the invoice: a price the
    /// office has to correct before the customer is billed for it. Once the
    /// warehouse has the order in hand the goods are moving, so it goes back to
    /// being the owner's decision alone.
    ///
    /// Everybody else asks, and the admin approves one change at a time -- see
    /// OrderChangeRequest.
    /// </summary>
    public static bool MayEditOrder(string roleKey, string statusKey) =>
        roleKey == RoleAdmin ||
        (roleKey == RoleAccountant && statusKey is Confirmed or Invoiced);

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
                /* ACCOUNTS IS THE ONE WHO ACTS NEXT. Confirming an order used
                   to tell the warehouse to "prepare the stock", which they
                   cannot do: their first move opens at Invoiced, and the
                   invoice is now the back office's to cut. So the accountant is
                   on this list, and the words say what is actually waited on.
                   The keeper is kept on it as a heads-up -- knowing an order is
                   coming is worth something even when there is nothing to
                   press yet. */
                new[] { RoleAdmin, RoleAccountant, RoleWarehouse, RoleSales },
                $"Order confirmed by {actor}",
                $"{orderNo} -- {customer}. Waiting for accounts to invoice it."),

            Declined => (NotificationKinds.OrderConfirmed,
                new[] { RoleAdmin, RoleSales },
                $"Order declined by {actor}",
                $"{orderNo} -- {customer} was declined."),

            /* The owner asked to be told, and so does the accountant who did it
               (their copy is suppressed by exceptUserId, so this reaches the
               other one). The keeper is here because THIS is the step that puts
               the order in their queue -- the invoice is cut and the stock can
               be picked. */
            Invoiced => (NotificationKinds.InvoiceRaised,
                new[] { RoleAdmin, RoleAccountant, RoleWarehouse },
                $"Order invoiced by {actor}",
                $"{orderNo} -- {customer} has been invoiced. The warehouse can pick it."),

            /* The keeper has the order in hand. The owner wants to know work
               has started; the rep wants to be able to tell the customer. The
               rep is added by the caller through alsoUserIds. */
            SeenByWarehouse => (NotificationKinds.OrderPacked,
                new[] { RoleAdmin },
                $"Order picked up by {actor}",
                $"{orderNo} -- {customer}. The warehouse has it and is preparing the stock."),

            /* This one the ORDER DEPARTMENT needs, because it is the moment
               something starts heading towards them. */
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
