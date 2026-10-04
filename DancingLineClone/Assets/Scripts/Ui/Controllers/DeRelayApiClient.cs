using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Ui.Controllers
{
    /// <summary>
    /// Thin HTTP client for the DeRelay social server (https://derelay.sunnygameai.site/).
    /// Uses UnityWebRequest + JsonUtility only (no external packages).
    /// Server contracts: PascalCase JSON fields; errors arrive as RFC ProblemDetails
    /// ({title, detail, status}) or ASP.NET validation ({title, status, errors{...}}).
    /// </summary>
    public static class DeRelayApiClient
    {
        public const string BaseUrl = "https://derelay.sunnygameai.site";

        /// <summary>When true, every DTO is logged as raw JSON on send and receive.</summary>
        public static bool LogTraffic = true;

        [Serializable] public class RegisterRequest
        {
            public string UserName = "";
            public string Password = "";
            public string FirstName = "";
            public string LastName = "";
            public string NickName = "";
            public int Gender;
            public string DateOfBirth = ""; // yyyy-MM-ddTHH:mm:ss
        }

        [Serializable] public class LoginRequest
        {
            public string UserName = "";
            public string Password = "";
        }

        [Serializable] public class UpdatePersonRequest
        {
            public string FirstName = "";
            public string LastName = "";
            public string NickName = "";
            public int Gender;
        }

        [Serializable] public class IdRequest
        {
            public int ReceiverId;
        }

        [Serializable] public class FriendIdRequest
        {
            public int FriendId;
        }

        // NOTE: the server serializes responses camelCase (ASP.NET default) and
        // Gender as a string ("Male"/"Female"/"Helicopter"). JsonUtility is
        // case-sensitive, so response fields must match the wire casing exactly.
        // (Request bodies can stay PascalCase: input parsing is case-insensitive.)
        [Serializable] public class ReturnPersonDto
        {
            public string firstName = "";
            public string lastName = "";
            public string nickName = "";
            public string gender = "";
            public string dateOfBirth = "";
            public int age;
        }

        [Serializable] public class IdListDto
        {
            // Server uses inconsistent names: userIds (friends) vs usersId (requests).
            public int[] userIds;
            public int[] usersId;
            public int[] Collect()
            {
                if (userIds != null) return userIds;
                if (usersId != null) return usersId;
                return Array.Empty<int>();
            }
        }

        [Serializable] private class TokenWrapper
        {
            public string token = "";
        }

        [Serializable] private class ProblemDetails
        {
            public string title = "";
            public string detail = "";
            public int status;
        }

        public class ApiResult<T>
        {
            public bool Ok;
            public T Data;
            public string ErrorMessage = "";
            public long StatusCode;
        }

        /// <summary>Extracts a user-friendly message from an error body.</summary>
        public static string ParseErrorMessage(string body)
        {
            if (string.IsNullOrEmpty(body)) return "Request failed with no response.";
            try
            {
                var problem = JsonUtility.FromJson<ProblemDetails>(body);
                if (problem != null && !string.IsNullOrEmpty(problem.detail)) return problem.detail;
            }
            catch { }
            // ASP.NET model-validation shape: {"errors":{"Field":["msg", ...]}, ...}
            try
            {
                int errorsIdx = body.IndexOf("\"errors\"", StringComparison.Ordinal);
                if (errorsIdx >= 0)
                {
                    int bracket = body.IndexOf('[', errorsIdx);
                    int quote1 = body.IndexOf('"', bracket);
                    int quote2 = body.IndexOf('"', quote1 + 1);
                    if (bracket >= 0 && quote1 >= 0 && quote2 > quote1)
                        return body.Substring(quote1 + 1, quote2 - quote1 - 1);
                }
            }
            catch { }
            try
            {
                var problem = JsonUtility.FromJson<ProblemDetails>(body);
                if (problem != null && !string.IsNullOrEmpty(problem.title)) return problem.title;
            }
            catch { }
            return "Request failed. Please try again.";
        }

        private static IEnumerator Send<T>(string method, string path, object body, string token, Action<ApiResult<T>> done)
        {
            var result = new ApiResult<T>();
            string url = BaseUrl + path;
            string json = body != null ? JsonUtility.ToJson(body) : null;
            if (LogTraffic) Debug.Log($"[DeRelay] SEND {method} {path}\n{json ?? "<no body>"}");
            using (var req = new UnityWebRequest(url, method))
            {
                if (json != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    req.uploadHandler = new UploadHandlerRaw(bytes);
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "application/json");
                if (!string.IsNullOrEmpty(token)) req.SetRequestHeader("Authorization", "Bearer " + token);
                yield return req.SendWebRequest();
                result.StatusCode = req.responseCode;
                string text = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (LogTraffic) Debug.Log($"[DeRelay] RECV {method} {path} [{req.responseCode}]\n{text}");
                if (req.result == UnityWebRequest.Result.Success && req.responseCode >= 200 && req.responseCode < 300)
                {
                    result.Ok = true;
                    result.Data = ConvertBody<T>(text);
                }
                else
                {
                    result.Ok = false;
                    result.ErrorMessage = ParseErrorMessage(text);
                }
            }
            done?.Invoke(result);
        }

        private static T ConvertBody<T>(string text)
        {
            if (typeof(T) == typeof(string))
            {
                // Login returns a bare JSON string: "eyJ...". Unwrap via helper object.
                object boxed = JsonUtility.FromJson<TokenWrapper>("{\"token\":" + text + "}");
                return (T)(object)((TokenWrapper)boxed).token;
            }
            if (typeof(T) == typeof(int))
            {
                object boxed = 0;
                int.TryParse(text.Trim(), out int parsed);
                boxed = parsed;
                return (T)boxed;
            }
            if (typeof(T) == typeof(bool))
            {
                object boxed = true;
                return (T)boxed;
            }
            if (string.IsNullOrEmpty(text)) return default(T);
            return JsonUtility.FromJson<T>(text);
        }

        private static IEnumerator Get<T>(string path, string token, Action<ApiResult<T>> done)
        {
            yield return Send<T>("GET", path, null, token, done);
        }

        private static IEnumerator Post<T>(string path, object body, string token, Action<ApiResult<T>> done)
        {
            yield return Send<T>("POST", path, body, token, done);
        }

        private static IEnumerator Put<T>(string path, object body, string token, Action<ApiResult<T>> done)
        {
            yield return Send<T>("PUT", path, body, token, done);
        }

        private static IEnumerator Delete<T>(string path, object body, string token, Action<ApiResult<T>> done)
        {
            yield return Send<T>("DELETE", path, body, token, done);
        }

        // ── Auth (no token) ──
        public static IEnumerator Register(RegisterRequest body, Action<ApiResult<int>> done)
        {
            yield return Post<int>("/api/auth/register", body, null, done);
        }

        public static IEnumerator Login(LoginRequest body, Action<ApiResult<string>> done)
        {
            yield return Post<string>("/api/auth/login", body, null, done);
        }

        public static IEnumerator DeleteAccount(string token, Action<ApiResult<bool>> done)
        {
            yield return Delete<bool>("/api/auth", null, token, done);
        }

        // ── Authed ──
        public static IEnumerator GetMe(string token, Action<ApiResult<ReturnPersonDto>> done)
        {
            yield return Get<ReturnPersonDto>("/api/persons", token, done);
        }

        public static IEnumerator GetFriends(string token, Action<ApiResult<IdListDto>> done)
        {
            yield return Get<IdListDto>("/api/friendship", token, done);
        }

        public static IEnumerator RemoveFriend(string token, int friendId, Action<ApiResult<bool>> done)
        {
            yield return Delete<bool>("/api/friendship", new FriendIdRequest { FriendId = friendId }, token, done);
        }

        public static IEnumerator SendFriendRequest(string token, int receiverId, Action<ApiResult<bool>> done)
        {
            yield return Post<bool>("/api/friendrequest", new IdRequest { ReceiverId = receiverId }, token, done);
        }

        public static IEnumerator AcceptFriendRequest(string token, int receiverId, Action<ApiResult<bool>> done)
        {
            yield return Post<bool>("/api/friendrequest/accept", new IdRequest { ReceiverId = receiverId }, token, done);
        }

        public static IEnumerator DeclineFriendRequest(string token, int receiverId, Action<ApiResult<bool>> done)
        {
            yield return Delete<bool>("/api/friendrequest", new IdRequest { ReceiverId = receiverId }, token, done);
        }

        public static IEnumerator GetSentRequests(string token, Action<ApiResult<IdListDto>> done)
        {
            yield return Get<IdListDto>("/api/friendrequest/sent", token, done);
        }

        public static IEnumerator GetReceivedRequests(string token, Action<ApiResult<IdListDto>> done)
        {
            yield return Get<IdListDto>("/api/friendrequest/receiver", token, done);
        }
    }
}
