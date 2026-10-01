// The Telegram login widget for linking a Telegram account to the Trust Score. Telegram's script
// draws its button into the container, and calls back with the signed login, which goes to .NET.
export function telegram(container, bot, dotnet) {
    if (!container || !bot) return;
    window.ndeipiTelegramAuth = user => dotnet.invokeMethodAsync('OnTelegramAsync', {
        id: user.id,
        firstName: user.first_name ?? null,
        lastName: user.last_name ?? null,
        username: user.username ?? null,
        photoUrl: user.photo_url ?? null,
        authDate: user.auth_date,
        hash: user.hash
    });
    container.innerHTML = '';
    const script = document.createElement('script');
    script.async = true;
    script.src = 'https://telegram.org/js/telegram-widget.js?22';
    script.setAttribute('data-telegram-login', bot);
    script.setAttribute('data-size', 'medium');
    script.setAttribute('data-radius', '10');
    script.setAttribute('data-userpic', 'false');
    script.setAttribute('data-onauth', 'ndeipiTelegramAuth(user)');
    container.appendChild(script);
}

