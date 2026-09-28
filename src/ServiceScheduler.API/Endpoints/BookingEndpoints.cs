using ServiceScheduler.Services.Booking;
using ServiceScheduler.Shared;

namespace ServiceScheduler.API.Endpoints;

public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/availability", async (
                Guid serviceTypeId,
                DateOnly date,
                IBookingService bookings,
                CancellationToken ct) =>
            {
                var result = await bookings.GetAvailabilityAsync(serviceTypeId, date, ct);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Problem(result.Error);
            })
            .WithName("GetAvailability")
            .WithSummary("Bookable start times for a service type on a date")
            .WithDescription(
                "Advisory: takes no locks and reserves nothing. A returned slot may be gone " +
                "before it is booked, which POST /bookings reports as 409.")
            .Produces<AvailabilityResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        app.MapPost("/bookings", async (
                CreateBookingCommand command,
                IBookingService bookings,
                CancellationToken ct) =>
            {
                var result = await bookings.CreateBookingAsync(command, ct);

                return result.IsSuccess
                    ? Results.Created($"/bookings/{result.Value!.BookingId}", result.Value)
                    : Problem(result.Error);
            })
            .WithName("CreateBooking")
            .WithSummary("Confirm an appointment")
            .WithDescription(
                "The client supplies a place and a time; the server assigns the service bay " +
                "and technician.")
            .Produces<BookingConfirmation>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static IResult Problem(BookingError error) => error switch
    {
        BookingError.NoCapacity => Results.Problem(
            title: "No capacity",
            detail: "No service bay or technician is free for the whole duration.",
            statusCode: StatusCodes.Status409Conflict),

        _ => Results.Problem(
            title: "Request cannot be processed",
            detail: error.ToString(),
            statusCode: StatusCodes.Status422UnprocessableEntity)
    };
}
