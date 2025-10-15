# ❤️ Support My Work

If this extension has been useful to you, consider supporting me.  
Every bit of support helps me improve the project and add new features. Thank you! 

&ensp;_Esta extensão foi útil para você? Considere me apoiar._
_Sua ajuda contribui a sempre poder trazer novas melhorias. Valeu!_<br>

<a href="https://www.buymeacoffee.com/Herion" target="_blank">
  <img src="https://www.buymeacoffee.com/assets/img/custom_images/yellow_img.png" 
       alt="Buy Me A Coffee" 
       style="height: 41px !important;width: 174px !important;box-shadow: 0px 3px 2px 0px rgba(190, 190, 190, 0.5) !important;-webkit-box-shadow: 0px 3px 2px 0px rgba(190, 190, 190, 0.5) !important;">
</a>

---

# Auto Description Localizer - Playnite Extension
Automatically translate your game descriptions with ease.
Translations are powered by [LibreTranslate](https://libretranslate.com/), providing fast and reliable results in multiple languages.  
**Supported Languages:** Brazilian Portuguese, Spanish, German, French, Russian, English  

 &ensp;_Traduz suas descrições de jogos automaticamente._  
 &ensp;_As traduções são fornecidas pelo LibreTranslate, oferecendo resultados rápidos e confiáveis em vários idiomas._<br>
 &ensp;*Idiomas suportados:* Português, Espanhol, Alemão, Francês, Russo, Inglês

---

## Questions, Suggestions, and Issues

If you encounter a problem, have a suggestion, or want to report an issue, please open a [new issue](https://github.com/For-otten/DescriptionLocalizer-PlayniteExtension/issues).  

 &ensp;_Se você encontrar um problema, tiver uma sugestão ou quiser reportar uma issue, abra uma new issue._

---

## Download

Download the latest release from GitHub:  
&ensp; _Baixe a versão mais recente no GitHub:_  
[Latest Release](https://github.com/For-otten/DescriptionLocalizer-PlayniteExtension/releases/latest)

Or directly from the [Playnite website](https://playnite.link/addons.html#FD25D279-1A13-4028-8438-E468110B28A6).  
 &ensp;_Ou diretamente do site do Playnite._

---

## Usage

- Choose your translation language dynamically, you don't need to restart the app  
   &ensp;_Escolha o idioma de tradução dinamicamente, não precisa reiniciar o aplicativo_

![Logo ADL](https://github.com/For-otten/DescriptionLocalizer-PlayniteExtension/raw/main/AutoDescriptionLocalizer/images/Captura%20de%20tela%202025-09-09%20032932.png)
- Decide whether to be asked before overwriting a translation  
   &ensp;_Decida se deseja ser perguntado antes de sobrescrever uma tradução_

![Logo ADL](https://github.com/For-otten/DescriptionLocalizer-PlayniteExtension/raw/main/AutoDescriptionLocalizer/images/Captura%20de%20tela%202025-09-09%20033726.png)

- The extension automatically detects and translates game descriptions:
  - When a new game is added to your library and it has a description  
  - When a description is modified
    
- It translates only if the description is not in your configured target language or the Playnite system language  
- Track translation progress through Playnite notifications  
- Supports up to **3 simultaneous translations**
<br><br>
 - _A extensão detecta e traduz automaticamente descrições de jogos:_
     - _Sempre que um novo jogo é adicionado à sua biblioteca e possui descrição_
     - _Sempre que uma descrição é alterada_
    
  - _A tradução ocorre apenas se a descrição não estiver no idioma configurado ou no idioma do sistema do Playnite_
  - _Acompanhe o progresso das traduções através das notificações do Playnite_
  - _Suporta até 3 traduções simultâneas_

---

## Contribution Guidelines

- If you plan to contribute, please configure your own LibreTranslate API or run a local server.  
- In your development environment, replace the `BaseUrl` variable:
  <br><br>
&ensp;_Para contribuir é necessário configurar uma API do LibreTranslate ou rodar em um servidor local._  
&ensp;_No seu ambiente de desenvolvimento, substitua a variável `BaseUrl`:_

```csharp
public static string BaseUrl = "YOUR_API";
