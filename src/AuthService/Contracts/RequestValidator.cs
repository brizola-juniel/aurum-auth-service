using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace AuthService.Contracts;

public static class RequestValidator
{
    public static IResult? Validate<TRequest>(TRequest? request)
    {
        if (request is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = new[] { "Request body is required." }
            });
        }

        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(request);
        if (Validator.TryValidateObject(request, validationContext, validationResults, validateAllProperties: true))
        {
            return null;
        }

        var errors = validationResults
            .SelectMany(result =>
            {
                var members = result.MemberNames.Any() ? result.MemberNames : new[] { "$" };
                return members.Select(member => new
                {
                    Member = member,
                    Message = result.ErrorMessage ?? "The field is invalid."
                });
            })
            .GroupBy(error => error.Member)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray());

        return Results.ValidationProblem(errors);
    }
}
