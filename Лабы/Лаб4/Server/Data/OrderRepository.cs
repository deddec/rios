using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server.Data;

public sealed record PlaceOrderOutcome(
    int OrderId,
    int BouquetId,
    int AvailableBefore,
    int AvailableAfter,
    bool UseTransaction,
    bool SimulatedError);
