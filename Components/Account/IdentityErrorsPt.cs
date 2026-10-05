using Microsoft.AspNetCore.Identity;

namespace DashTudo.Web.Components.Account;

/// <summary>Mensagens do Identity em português.</summary>
internal static class IdentityErrorsPt
{
    public static string Translate(IdentityError e) => e.Code switch
    {
        "DuplicateUserName" or "DuplicateEmail" => "Já existe uma conta com este email.",
        "InvalidEmail" or "InvalidUserName" => "Email inválido.",
        "PasswordTooShort" => "A senha é muito curta.",
        "PasswordRequiresDigit" => "A senha precisa conter ao menos um número.",
        "PasswordRequiresLower" => "A senha precisa conter ao menos uma letra minúscula.",
        "PasswordRequiresUpper" => "A senha precisa conter ao menos uma letra maiúscula.",
        "PasswordRequiresNonAlphanumeric" => "A senha precisa conter ao menos um símbolo.",
        "PasswordMismatch" => "Senha atual incorreta.",
        _ => e.Description,
    };
}
