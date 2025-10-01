using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Portal.Shared.Web.Infrastructure;

internal static class PortalIcons
{
    internal static class Size20
    {
        // The official SVGs from GitHub have a viewbox of 96x96, so we need to scale them down to 20x20 and center them within the 24x24 box to make them match the
        // other icons we're using. We also need to remove the fill attribute from the SVGs so that we can color them with CSS. 
        internal sealed class GitHub() : Icon("GitHub", IconVariant.Filled, IconSize.Size20,
            @"<path fill-rule=""evenodd"" clip-rule=""evenodd"" d=""M10.178 0C4.55 0 0 4.583 0 10.254c0 4.533 2.915 8.369 6.959 9.727 0.506 0.102 0.691-0.221 0.691-0.492 0-0.238-0.017-1.053-0.017-1.901-2.831 0.611-3.421-1.222-3.421-1.222-0.455-1.188-1.129-1.494-1.129-1.494-0.927-0.628 0.068-0.628 0.068-0.628 1.028 0.068 1.567 1.053 1.567 1.053 0.91 1.562 2.376 1.12 2.966 0.849 0.084-0.662 0.354-1.12 0.64-1.375-2.258-0.238-4.634-1.12-4.634-5.059 0-1.12 0.404-2.037 1.045-2.75-0.101-0.255-0.455-1.307 0.101-2.716 0 0 0.859-0.272 2.797 1.053a9.786 9.786 0 0 1 2.545-0.34c0.859 0 1.735 0.119 2.544 0.34 1.938-1.324 2.797-1.053 2.797-1.053 0.556 1.409 0.202 2.462 0.101 2.716 0.657 0.713 1.045 1.63 1.045 2.75 0 3.939-2.376 4.804-4.651 5.059 0.371 0.323 0.691 0.934 0.691 1.901 0 1.375-0.017 2.479-0.017 2.818 0 0.272 0.185 0.594 0.691 0.493 4.044-1.358 6.959-5.195 6.959-9.727C20.356 4.583 15.789 0 10.178 0z""/>");

        internal sealed class Discord() : Icon("Discord", IconVariant.Filled, IconSize.Size20,
            // Adjusted transform: The chain translates the original viewBox (0, -28.5, 256, 256) so its origin moves to (0,0), scales it down to 20x20 (scale factor 20/256 = 0.078125)
            // and then shifts it 1px right and 2px down within the 24x24 box.
            @"<path transform=""translate(1,2) scale(0.078125) translate(0,28.5)"" fill-rule=""evenodd"" clip-rule=""evenodd"" d=""M216.856339,16.5966031 C200.285002,8.84328665 182.566144,3.2084988 164.041564,0 C161.766523,4.11318106 159.108624,9.64549908 157.276099,14.0464379 C137.583995,11.0849896 118.072967,11.0849896 98.7430163,14.0464379 C96.9108417,9.64549908 94.1925838,4.11318106 91.8971895,0 C73.3526068,3.2084988 55.6133949,8.86399117 39.0420583,16.6376612 C5.61752293,67.146514 -3.4433191,116.400813 1.08711069,164.955721 C23.2560196,181.510915 44.7403634,191.567697 65.8621325,198.148576 C71.0772151,190.971126 75.7283628,183.341335 79.7352139,175.300261 C72.104019,172.400575 64.7949724,168.822202 57.8887866,164.667963 C59.7209612,163.310589 61.5131304,161.891452 63.2445898,160.431257 C105.36741,180.133187 151.134928,180.133187 192.754523,160.431257 C194.506336,161.891452 196.298154,163.310589 198.110326,164.667963 C191.183787,168.842556 183.854737,172.420929 176.223542,175.320965 C180.230393,183.341335 184.861538,190.991831 190.096624,198.16893 C211.238746,191.588051 232.743023,181.531619 254.911949,164.955721 C260.227747,108.668201 245.831087,59.8662432 216.856339,16.5966031 Z M85.4738752,135.09489 C72.8290281,135.09489 62.4592217,123.290155 62.4592217,108.914901 C62.4592217,94.5396472 72.607595,82.7145587 85.4738752,82.7145587 C98.3405064,82.7145587 108.709962,94.5189427 108.488529,108.914901 C108.508531,123.290155 98.3405064,135.09489 85.4738752,135.09489 Z M170.525237,135.09489 C157.88039,135.09489 147.510584,123.290155 147.510584,108.914901 C147.510584,94.5396472 157.658606,82.7145587 170.525237,82.7145587 C183.391518,82.7145587 193.761324,94.5189427 193.539891,108.914901 C193.539891,123.290155 183.391518,135.09489 170.525237,135.09489 Z""/>");
    }

    internal static class Size24
    {
        internal sealed class GitHub() : Icon("GitHub", IconVariant.Filled, IconSize.Size24,
            @"<path transform='scale(1.2)' fill-rule='evenodd' clip-rule='evenodd' d='M10.178 0C4.55 0 0 4.583 0 10.254c0 4.533 2.915 8.369 6.959 9.727 0.506 0.102 0.691-0.221 0.691-0.492 0-0.238-0.017-1.053-0.017-1.901-2.831 0.611-3.421-1.222-3.421-1.222-0.455-1.188-1.129-1.494-1.129-1.494-0.927-0.628 0.068-0.628 0.068-0.628 1.028 0.068 1.567 1.053 1.567 1.053 0.91 1.562 2.376 1.12 2.966 0.849 0.084-0.662 0.354-1.12 0.64-1.375-2.258-0.238-4.634-1.12-4.634-5.059 0-1.12 0.404-2.037 1.045-2.75-0.101-0.255-0.455-1.307 0.101-2.716 0 0 0.859-0.272 2.797 1.053a9.786 9.786 0 0 1 2.545-0.34c0.859 0 1.735 0.119 2.544 0.34 1.938-1.324 2.797-1.053 2.797-1.053 0.556 1.409 0.202 2.462 0.101 2.716 0.657 0.713 1.045 1.63 1.045 2.75 0 3.939-2.376 4.804-4.651 5.059 0.371 0.323 0.691 0.934 0.691 1.901 0 1.375-0.017 2.479-0.017 2.818 0 0.272 0.185 0.594 0.691 0.493 4.044-1.358 6.959-5.195 6.959-9.727C20.356 4.583 15.789 0 10.178 0z'/>");

        internal sealed class Discord() : Icon("Discord", IconVariant.Filled, IconSize.Size24,
            @"<path transform='translate(0,3) scale(0.09375) translate(0,28.5)' fill-rule='evenodd' clip-rule='evenodd' d='M216.856339,16.5966031 C200.285002,8.84328665 182.566144,3.2084988 164.041564,0 C161.766523,4.11318106 159.108624,9.64549908 157.276099,14.0464379 C137.583995,11.0849896 118.072967,11.0849896 98.7430163,14.0464379 C96.9108417,9.64549908 94.1925838,4.11318106 91.8971895,0 C73.3526068,3.2084988 55.6133949,8.86399117 39.0420583,16.6376612 C5.61752293,67.146514 -3.4433191,116.400813 1.08711069,164.955721 C23.2560196,181.510915 44.7403634,191.567697 65.8621325,198.148576 C71.0772151,190.971126 75.7283628,183.341335 79.7352139,175.300261 C72.104019,172.400575 64.7949724,168.822202 57.8887866,164.667963 C59.7209612,163.310589 61.5131304,161.891452 63.2445898,160.431257 C105.36741,180.133187 151.134928,180.133187 192.754523,160.431257 C194.506336,161.891452 196.298154,163.310589 198.110326,164.667963 C191.183787,168.842556 183.854737,172.420929 176.223542,175.320965 C180.230393,183.341335 184.861538,190.991831 190.096624,198.16893 C211.238746,191.588051 232.743023,181.531619 254.911949,164.955721 C260.227747,108.668201 245.831087,59.8662432 216.856339,16.5966031 Z M85.4738752,135.09489 C72.8290281,135.09489 62.4592217,123.290155 62.4592217,108.914901 C62.4592217,94.5396472 72.607595,82.7145587 85.4738752,82.7145587 C98.3405064,82.7145587 108.709962,94.5189427 108.488529,108.914901 C108.508531,123.290155 98.3405064,135.09489 85.4738752,135.09489 Z M170.525237,135.09489 C157.88039,135.09489 147.510584,123.290155 147.510584,108.914901 C147.510584,94.5396472 157.658606,82.7145587 170.525237,82.7145587 C183.391518,82.7145587 193.761324,94.5189427 193.539891,108.914901 C193.539891,123.290155 183.391518,135.09489 170.525237,135.09489 Z'/>");

        internal sealed class YouTube() : Icon("YouTube", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.05205)'>
        <path fill-rule='evenodd' clip-rule='evenodd' d='M365.257,67.393H95.744C42.866,67.393,0,110.259,0,163.137v134.728 c0,52.878,42.866,95.744,95.744,95.744h269.513c52.878,0,95.744-42.866,95.744-95.744V163.137 C461.001,110.259,418.135,67.393,365.257,67.393z'/>
        <path fill-rule='evenodd' clip-rule='evenodd' fill='#FFF' d='M300.506,237.056l-126.06,60.123c-3.359,1.602-7.239-0.847-7.239-4.568V168.607 c0-3.774,3.982-6.22,7.348-4.514l126.06,63.881C304.363,229.873,304.298,235.248,300.506,237.056z'/>
     </g>");

        internal sealed class LinkedIn() : Icon("LinkedIn", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.07742)'>
        <path fill-rule='evenodd' clip-rule='evenodd' d='M72.16,99.73H9.927c-2.762,0-5,2.239-5,5v199.928c0,2.762,2.238,5,5,5H72.16 c2.762,0,5-2.238,5-5V104.73 C77.16,101.969,74.922,99.73,72.16,99.73z'/>
        <path fill-rule='evenodd' clip-rule='evenodd' d='M41.066,0.341C18.422,0.341,0,18.743,0,41.362 C0,63.991,18.422,82.4,41.066,82.4c22.626,0,41.033-18.41,41.033-41.038 C82.1,18.743,63.692,0.341,41.066,0.341z'/>
        <path fill-rule='evenodd' clip-rule='evenodd' d='M230.454,94.761c-24.995,0-43.472,10.745-54.679,22.954V104.73 c0-2.761-2.238-5-5-5h-59.599 c-2.762,0-5,2.239-5,5v199.928c0,2.762,2.238,5,5,5h62.097c2.762,0,5-2.238,5-5v-98.918c0-33.333,9.054-46.319,32.29-46.319 c25.306,0,27.317,20.818,27.317,48.034v97.204c0,2.762,2.238,5,5,5H305c2.762,0,5-2.238,5-5V194.995 C310,145.43,300.549,94.761,230.454,94.761z'/>
    </g>"
        );

        internal sealed class Bluesky() : Icon("Bluesky", IconVariant.Filled, IconSize.Size24,
            @"<path transform='scale(0.04)' class='bluesky-icon' d='M299.75 238.48c-26.326-51.01-97.736-146.28-164.21-193.17-63.677-44.92-88.028-37.19-103.82-29.95-18.428 8.39-21.719 36.69-21.719 53.31s9.05 136.57 15.138 156.48c19.745 66.15 89.674 88.52 154.17 81.28 3.29-0.49 6.58-0.99 10.04-1.32-3.29 0.49-6.75 0.99-10.04 1.32-94.445 13.99-178.52 48.37-68.284 170.96 121.1 125.38 166.02-26.82 189.06-104.15 23.035 77.17 49.526 223.94 186.75 104.15 103.17-104.15 28.301-156.97-66.145-170.96-3.29-0.33-6.75-0.82-10.04-1.32 3.46 0.49 6.75 0.82 10.04 1.32 64.499 7.24 134.59-15.14 154.17-81.28 5.92-20.07 15.14-139.86 15.14-156.48s-3.29-44.92-21.72-53.31c-15.96-7.24-40.15-14.97-103.82 29.95-66.97 47.06-138.38 142.16-164.7 193.17z'/>");
        internal sealed class X() : Icon("X", IconVariant.Filled, IconSize.Size24,
            @"<path class='x-icon' d='M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-5.214-6.817L4.99 21.75H1.68l7.73-8.835L1.254 2.25H8.08l4.713 6.231zm-1.161 17.52h1.833L7.084 4.126H5.117z'/>");

        internal sealed class Threads() : Icon("Threads", IconVariant.Filled, IconSize.Size24,
            @"<path transform='scale(0.125)' class='threads-icon' d='M141.537 88.9883C140.71 88.5919 139.87 88.2104 139.019 87.8451C137.537 60.5382 122.616 44.905 97.5619 44.745C97.4484 44.7443 97.3355 44.7443 97.222 44.7443C82.2364 44.7443 69.7731 51.1409 62.102 62.7807L75.881 72.2328C81.6116 63.5383 90.6052 61.6848 97.2286 61.6848C97.3051 61.6848 97.3819 61.6848 97.4576 61.6855C105.707 61.7381 111.932 64.1366 115.961 68.814C118.893 72.2193 120.854 76.925 121.825 82.8638C114.511 81.6207 106.601 81.2385 98.145 81.7233C74.3247 83.0954 59.0111 96.9879 60.0396 116.292C60.5615 126.084 65.4397 134.508 73.775 140.011C80.8224 144.663 89.899 146.938 99.3323 146.423C111.79 145.74 121.563 140.987 128.381 132.296C133.559 125.696 136.834 117.143 138.28 106.366C144.217 109.949 148.617 114.664 151.047 120.332C155.179 129.967 155.42 145.8 142.501 158.708C131.182 170.016 117.576 174.908 97.0135 175.059C74.2042 174.89 56.9538 167.575 45.7381 153.317C35.2355 139.966 29.8077 120.682 29.6052 96C29.8077 71.3178 35.2355 52.0336 45.7381 38.6827C56.9538 24.4249 74.2039 17.11 97.0132 16.9405C119.988 17.1113 137.539 24.4614 149.184 38.788C154.894 45.8136 159.199 54.6488 162.037 64.9503L178.184 60.6422C174.744 47.9622 169.331 37.0357 161.965 27.974C147.036 9.60668 125.202 0.195148 97.0695 0H96.9569C68.8816 0.19447 47.2921 9.6418 32.7883 28.0793C19.8819 44.4864 13.2244 67.3157 13.0007 95.9325L13 96L13.0007 96.0675C13.2244 124.684 19.8819 147.514 32.7883 163.921C47.2921 182.358 68.8816 191.806 96.9569 192H97.0695C122.03 191.827 139.624 185.292 154.118 170.811C173.081 151.866 172.51 128.119 166.26 113.541C161.776 103.087 153.227 94.5962 141.537 88.9883ZM98.4405 129.507C88.0005 130.095 77.1544 125.409 76.6196 115.372C76.2232 107.93 81.9158 99.626 99.0812 98.6368C101.047 98.5234 102.976 98.468 104.871 98.468C111.106 98.468 116.939 99.0737 122.242 100.233C120.264 124.935 108.662 128.946 98.4405 129.507Z'/>");

        internal sealed class Northwind() : Icon("Northwind", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.2)'>
                <g stroke='currentColor' stroke-width='6' stroke-linejoin='round' stroke-linecap='round' fill='none'>
                    <rect x='54' y='32' width='12' height='20' fill='currentColor'/>
                    <path d='M60 52 L48 108 L72 108 Z' fill='currentColor'/>
                    <path d='M60 20 L60 8' />
                    <path d='M60 20 L44 14' />
                    <path d='M60 20 L76 14' />
                </g>
                <g fill='currentColor'>
                    <path d='M20 112 Q40 104 60 112 T100 112 Q80 120 60 112 T20 112 Z' fill-opacity='0.35'/>
                </g>
                <g font-family='Arial, sans-serif' font-weight='bold' font-size='22' text-anchor='middle' fill='currentColor'>
                    <text x='60' y='110' style='letter-spacing:1px'>NORTHWIND</text>
                </g>
            </g>");

        internal sealed class NorthwindActive() : Icon("NorthwindActive", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.2)'>
                <g stroke='currentColor' stroke-width='6' stroke-linejoin='round' stroke-linecap='round' fill='none'>
                    <rect x='54' y='32' width='12' height='20' fill='currentColor'/>
                    <path d='M60 52 L48 108 L72 108 Z' fill='currentColor'/>
                    <path d='M60 20 L60 4' />
                    <path d='M60 20 L40 10' />
                    <path d='M60 20 L80 10' />
                    <path d='M60 26 L34 22' stroke-opacity='0.6' />
                    <path d='M60 26 L86 22' stroke-opacity='0.6' />
                </g>
                <g fill='currentColor'>
                    <path d='M20 112 Q40 104 60 112 T100 112 Q80 120 60 112 T20 112 Z' fill-opacity='0.5'/>
                </g>
                <g font-family='Arial, sans-serif' font-weight='bold' font-size='22' text-anchor='middle' fill='currentColor'>
                    <text x='60' y='110' style='letter-spacing:1px'>NORTHWIND</text>
                </g>
            </g>");

        // Northwind Article Icon
        internal sealed class NorthwindArticle() : Icon("NorthwindArticle", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.2)'>
                <g stroke='currentColor' stroke-width='6' stroke-linejoin='round' stroke-linecap='round' fill='none'>
                    <rect x='54' y='32' width='12' height='20' fill='currentColor'/>
                    <path d='M60 52 L48 108 L72 108 Z' fill='currentColor'/>
                    <path d='M60 20 L60 8' />
                    <path d='M60 20 L44 14' />
                    <path d='M60 20 L76 14' />
                </g>
                <g fill='currentColor'>
                    <path d='M20 112 Q40 104 60 112 T100 112 Q80 120 60 112 T20 112 Z' fill-opacity='0.35'/>
                </g>
                <g font-family='Arial, sans-serif' font-weight='bold' font-size='22' text-anchor='middle' fill='currentColor'>
                    <text x='60' y='110' style='letter-spacing:1px'>NORTHWIND</text>
                </g>
                <!-- Document overlay -->
                <rect x='80' y='60' width='28' height='36' rx='4' fill='white' stroke='currentColor' stroke-width='3'/>
                <line x1='85' y1='70' x2='102' y2='70' stroke='currentColor' stroke-width='2'/>
                <line x1='85' y1='78' x2='102' y2='78' stroke='currentColor' stroke-width='2'/>
            </g>");

        // Northwind Article Active (Stealth) Icon
        internal sealed class NorthwindArticleActive() : Icon("NorthwindArticleActive", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.2)'>
                <g stroke='currentColor' stroke-width='6' stroke-linejoin='round' stroke-linecap='round' fill='none'>
                    <rect x='54' y='32' width='12' height='20' fill='var(--stealth-color, #E5E5F6)'/>
                    <path d='M60 52 L48 108 L72 108 Z' fill='var(--stealth-color, #E5E5F6)'/>
                    <path d='M60 20 L60 4' />
                    <path d='M60 20 L40 10' />
                    <path d='M60 20 L80 10' />
                    <path d='M60 26 L34 22' stroke-opacity='0.6' />
                    <path d='M60 26 L86 22' stroke-opacity='0.6' />
                </g>
                <g fill='var(--stealth-color, #E5E5F6)'>
                    <path d='M20 112 Q40 104 60 112 T100 112 Q80 120 60 112 T20 112 Z' fill-opacity='0.5'/>
                </g>
                <g font-family='Arial, sans-serif' font-weight='bold' font-size='22' text-anchor='middle' fill='var(--stealth-color, #E5E5F6)'>
                    <text x='60' y='110' style='letter-spacing:1px'>NORTHWIND</text>
                </g>
                <!-- Document overlay with stealth color -->
                <rect x='80' y='60' width='28' height='36' rx='4' fill='var(--stealth-color, #E5E5F6)' stroke='currentColor' stroke-width='3'/>
                <line x1='85' y1='70' x2='102' y2='70' stroke='currentColor' stroke-width='2'/>
                <line x1='85' y1='78' x2='102' y2='78' stroke='currentColor' stroke-width='2'/>
            </g>");

        // Todo Article Icon
        internal sealed class TodoArticle() : Icon("TodoArticle", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.2)'>
                <!-- Main Todo logo (simple checkmark in a box) -->
                <rect x='40' y='40' width='40' height='40' rx='8' fill='currentColor' fill-opacity='0.2' stroke='currentColor' stroke-width='6'/>
                <polyline points='50,60 60,80 80,50' fill='none' stroke='currentColor' stroke-width='6' stroke-linecap='round' stroke-linejoin='round'/>
                <!-- Document overlay -->
                <rect x='80' y='60' width='28' height='36' rx='4' fill='white' stroke='currentColor' stroke-width='3'/>
                <line x1='85' y1='70' x2='102' y2='70' stroke='currentColor' stroke-width='2'/>
                <line x1='85' y1='78' x2='102' y2='78' stroke='currentColor' stroke-width='2'/>
            </g>");

        // Todo Article Active (Stealth) Icon
        internal sealed class TodoArticleActive() : Icon("TodoArticleActive", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.2)'>
                <rect x='40' y='40' width='40' height='40' rx='8' fill='var(--stealth-color, #E5E5F6)' stroke='currentColor' stroke-width='6'/>
                <polyline points='50,60 60,80 80,50' fill='none' stroke='currentColor' stroke-width='6' stroke-linecap='round' stroke-linejoin='round'/>
                <!-- Document overlay with stealth color -->
                <rect x='80' y='60' width='28' height='36' rx='4' fill='var(--stealth-color, #E5E5F6)' stroke='currentColor' stroke-width='3'/>
                <line x1='85' y1='70' x2='102' y2='70' stroke='currentColor' stroke-width='2'/>
                <line x1='85' y1='78' x2='102' y2='78' stroke='currentColor' stroke-width='2'/>
            </g>");

        // Documentation Article Icon
        internal sealed class DocumentationArticle() : Icon("DocumentationArticle", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.2)'>
                <!-- Main Documentation logo (book) -->
                <rect x='40' y='40' width='40' height='50' rx='6' fill='currentColor' fill-opacity='0.15' stroke='currentColor' stroke-width='6'/>
                <line x1='50' y1='50' x2='50' y2='80' stroke='currentColor' stroke-width='4'/>
                <line x1='70' y1='50' x2='70' y2='80' stroke='currentColor' stroke-width='4'/>
                <line x1='50' y1='65' x2='70' y2='65' stroke='currentColor' stroke-width='2'/>
                <!-- Document overlay -->
                <rect x='80' y='60' width='28' height='36' rx='4' fill='white' stroke='currentColor' stroke-width='3'/>
                <line x1='85' y1='70' x2='102' y2='70' stroke='currentColor' stroke-width='2'/>
                <line x1='85' y1='78' x2='102' y2='78' stroke='currentColor' stroke-width='2'/>
            </g>");

        // Documentation Article Active (Stealth) Icon
        internal sealed class DocumentationArticleActive() : Icon("DocumentationArticleActive", IconVariant.Filled, IconSize.Size24,
            @"<g transform='scale(0.2)'>
                <rect x='40' y='40' width='40' height='50' rx='6' fill='var(--stealth-color, #E5E5F6)' stroke='currentColor' stroke-width='6'/>
                <line x1='50' y1='50' x2='50' y2='80' stroke='currentColor' stroke-width='4'/>
                <line x1='70' y1='50' x2='70' y2='80' stroke='currentColor' stroke-width='4'/>
                <line x1='50' y1='65' x2='70' y2='65' stroke='currentColor' stroke-width='2'/>
                <!-- Document overlay with stealth color -->
                <rect x='80' y='60' width='28' height='36' rx='4' fill='var(--stealth-color, #E5E5F6)' stroke='currentColor' stroke-width='3'/>
                <line x1='85' y1='70' x2='102' y2='70' stroke='currentColor' stroke-width='2'/>
                <line x1='85' y1='78' x2='102' y2='78' stroke='currentColor' stroke-width='2'/>
            </g>");
    }
}

