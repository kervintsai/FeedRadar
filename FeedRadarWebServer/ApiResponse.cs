public record ApiResponse<T>(bool Success, T Data)
{
    public static ApiResponse<T> Ok(T data) => new(true, data);
}

public record ApiError(string Code, string Message);
public record ApiErrorResponse(bool Success, ApiError Error);
