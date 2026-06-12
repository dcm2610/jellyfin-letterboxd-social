(function () {
    'use strict';

    const widgetClass = 'letterboxd-social-widget';
    const styleId = 'letterboxd-social-widget-styles';
    const renderDelay = 250;
    const retryDelays = [650, 1400, 2600];

    // activeItemId: movie currently in flight or rendered (null = idle)
    // activeId:     incremented each new render; stale completions bail when theirs no longer matches
    // lastReviews:  loaded reviews for activeItemId, used to re-place the widget if Jellyfin
    //               re-renders the page and drops our DOM without a navigation event
    let activeItemId = null;
    let activeId = 0;
    let lastReviews = null;
    let renderTimer = 0;

    function getActiveDetailPage() {
        const pages = Array.from(document.querySelectorAll('.itemDetailPage, .itemdetailpage'));
        if (pages.length === 0) {
            return null;
        }

        const activePages = pages.filter(function (page) {
            const classes = page.className || '';
            const style = window.getComputedStyle ? window.getComputedStyle(page) : null;
            return !classes.toLowerCase().split(/\s+/).includes('hide')
                && page.hidden !== true
                && page.getAttribute('aria-hidden') !== 'true'
                && (!style || (style.display !== 'none' && style.visibility !== 'hidden'));
        });

        return (activePages.length > 0 ? activePages : pages)[(activePages.length > 0 ? activePages : pages).length - 1];
    }

    function getItemIdFromUrl() {
        const candidates = [
            window.location.search,
            window.location.hash.includes('?') ? window.location.hash.slice(window.location.hash.indexOf('?')) : '',
            window.location.hash.includes('&') ? '?' + window.location.hash.split('&').slice(1).join('&') : ''
        ];

        for (const candidate of candidates) {
            if (!candidate) {
                continue;
            }

            const params = new URLSearchParams(candidate.startsWith('?') ? candidate : '?' + candidate);
            const id = params.get('id') || params.get('itemId') || params.get('itemid');
            if (id) {
                return id;
            }
        }

        return null;
    }

    function getCurrentItemId() {
        const page = getActiveDetailPage();
        if (page) {
            const pageId = page.getAttribute('data-itemid')
                || page.getAttribute('data-itemId')
                || page.getAttribute('data-id');
            if (pageId) {
                return pageId;
            }

            const nested = page.querySelector('[data-itemid], [data-id]');
            if (nested) {
                const nestedId = nested.getAttribute('data-itemid') || nested.getAttribute('data-id');
                if (nestedId) {
                    return nestedId;
                }
            }
        }

        return getItemIdFromUrl();
    }

    function isMovieDetailPage() {
        const page = getActiveDetailPage();
        if (!page) {
            return false;
        }

        const classes = page.className || '';
        if (classes.toLowerCase().includes('itemdetailpage')) {
            return true;
        }

        return !!getCurrentItemId();
    }

    async function getCurrentItem(itemId) {
        if (!itemId || !window.ApiClient) {
            return null;
        }

        try {
            if (typeof window.ApiClient.getCurrentUserId === 'function' && typeof window.ApiClient.getItem === 'function') {
                const userId = window.ApiClient.getCurrentUserId();
                return await window.ApiClient.getItem(userId, itemId);
            }

            if (typeof window.ApiClient.ajax === 'function' && typeof window.ApiClient.getUrl === 'function') {
                return await window.ApiClient.ajax({
                    type: 'GET',
                    url: window.ApiClient.getUrl('Items/' + encodeURIComponent(itemId)),
                    dataType: 'json'
                });
            }
        } catch (error) {
            console.warn('Letterboxd Social: failed to read current item.', error);
        }

        return null;
    }

    function getApiUrl(movieId) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl('ScheduledLetterboxd/Reviews', { movieId });
        }

        const base = document.querySelector('base[href]');
        const prefix = base ? base.getAttribute('href').replace(/\/$/, '') : '';
        return prefix + '/ScheduledLetterboxd/Reviews?movieId=' + encodeURIComponent(movieId);
    }

    async function fetchReviews(movieId) {
        const url = getApiUrl(movieId);

        if (window.ApiClient && typeof window.ApiClient.ajax === 'function') {
            return normalizeReviews(await window.ApiClient.ajax({
                type: 'GET',
                url,
                dataType: 'json'
            }));
        }

        const headers = {};
        const token = window.ApiClient && typeof window.ApiClient.accessToken === 'function'
            ? window.ApiClient.accessToken()
            : null;

        if (token) {
            headers['X-Emby-Token'] = token;
        }

        const response = await fetch(url, {
            credentials: 'same-origin',
            headers
        });

        if (!response.ok) {
            return [];
        }

        return normalizeReviews(await response.json());
    }

    function normalizeReviews(value) {
        if (Array.isArray(value)) {
            return value;
        }

        if (typeof value === 'string') {
            try {
                return normalizeReviews(JSON.parse(value));
            } catch {
                return [];
            }
        }

        if (value && Array.isArray(value.Items)) {
            return value.Items;
        }

        if (value && Array.isArray(value.items)) {
            return value.items;
        }

        if (value && Array.isArray(value.value)) {
            return value.value;
        }

        return [];
    }

    function getValue(source, camelName, pascalName) {
        if (!source) {
            return undefined;
        }

        return source[camelName] !== undefined ? source[camelName] : source[pascalName];
    }

    function findCastAndCrewHeading(page) {
        const headings = page.querySelectorAll('h2, h3, .sectionTitle, .sectionTitleText, .detailSectionHeader');
        return Array.from(headings).find(function (heading) {
            const text = (heading.textContent || '').trim().toLowerCase();
            return text === 'cast & crew' || text === 'cast' || text.includes('cast & crew');
        }) || null;
    }

    function findCastAndCrewSection(page) {
        const heading = findCastAndCrewHeading(page);
        if (!heading) {
            return null;
        }

        const candidates = [
            heading.closest('.verticalSection'),
            heading.closest('.detailSection'),
            heading.closest('.peopleSection'),
            heading.closest('.section'),
            heading.parentElement && heading.parentElement.parentElement,
            heading.parentElement
        ];

        for (const candidate of candidates) {
            if (!candidate || candidate === page || candidate.classList.contains(widgetClass)) {
                continue;
            }

            if (candidate.querySelector('.itemsContainer, .emby-scroller, .swiper, .cardScalable, .personCard, button')) {
                return candidate;
            }
        }

        return heading.parentElement || heading;
    }

    function placeWidget(page, wrapper) {
        const castSection = findCastAndCrewSection(page);
        if (castSection && castSection.parentNode) {
            castSection.parentNode.insertBefore(wrapper, castSection);
            return;
        }

        const metadataTargets = [
            '.itemDetailsGroup',
            '.detailSectionContent',
            '.itemDetailPageContent',
            '.detailPageContent'
        ];

        for (const selector of metadataTargets) {
            const target = page.querySelector(selector);
            if (target) {
                target.insertAdjacentElement('afterend', wrapper);
                return;
            }
        }

        page.appendChild(wrapper);
    }

    function ensureWidgetStyles() {
        if (document.getElementById(styleId)) {
            return;
        }

        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = [
            '.letterboxd-social-widget{box-sizing:border-box;width:100%;max-width:100%;margin:1.8rem 0 2.1rem;padding:.82rem .9rem .95rem;background:var(--selectorBackgroundColorAlpha,rgba(31,35,42,.55));border:1px solid var(--borderColor,rgba(255,255,255,.11));border-radius:8px;box-shadow:0 14px 38px rgba(0,0,0,.16);backdrop-filter:blur(10px);color:rgba(255,255,255,.88);clear:both;font-family:Inter,inherit;}',
            '.letterboxd-social-widget *{box-sizing:border-box;}',
            '.letterboxd-widget-title{display:flex;align-items:center;gap:.48rem;margin:0 0 .75rem;font-size:.98rem;font-weight:800;line-height:1.2;color:rgba(255,255,255,.92);}',
            '.letterboxd-widget-title:before{content:"";display:block;width:.22rem;height:1rem;border-radius:999px;background:#00e054;}',
            '.letterboxd-review-list{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,28rem),1fr));gap:.62rem;}',
            '.letterboxd-user-card{min-width:0;overflow:hidden;padding:.68rem .74rem .72rem;background:rgba(0,0,0,.13);border:1px solid var(--lighterBorderColor,rgba(255,255,255,.10));border-radius:7px;}',
            '.letterboxd-empty-state{grid-column:1 / -1;min-width:0;padding:.72rem .78rem;background:rgba(0,0,0,.10);border:1px solid var(--lighterBorderColor,rgba(255,255,255,.10));border-radius:7px;color:var(--dimTextColor,rgba(255,255,255,.68));font-size:.9rem;font-weight:600;line-height:1.4;}',
            '.letterboxd-loading-state{display:flex;align-items:center;gap:.62rem;width:100%;min-width:0;margin-top:.62rem;padding:.72rem .78rem;background:rgba(0,0,0,.10);border:1px solid var(--lighterBorderColor,rgba(255,255,255,.10));border-radius:7px;color:var(--dimTextColor,rgba(255,255,255,.68));font-size:.9rem;font-weight:700;line-height:1.4;}',
            '.letterboxd-loading-spinner{flex:0 0 auto;width:1rem;height:1rem;border-radius:50%;border:2px solid rgba(255,255,255,.18);border-top-color:#00e054;animation:letterboxd-spin .85s linear infinite;}',
            '@keyframes letterboxd-spin{to{transform:rotate(360deg);}}',
            '.letterboxd-user-heading{display:grid;grid-template-columns:minmax(0,1fr) max-content;align-items:start;gap:.72rem;margin:0 0 .42rem;}',
            '.letterboxd-user-identity{display:flex;align-items:center;gap:.48rem;min-width:0;}',
            '.letterboxd-user-avatar{flex:0 0 auto;width:1.72rem;height:1.72rem;border-radius:50%;object-fit:cover;background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.16);box-shadow:0 2px 8px rgba(0,0,0,.22);}',
            '.letterboxd-user-name{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:.9rem;font-weight:800;color:rgba(255,255,255,.9);}',
            '.letterboxd-user-rating{display:inline-flex;align-items:center;justify-content:flex-end;min-width:4.2rem;font-size:.88rem;font-weight:900;line-height:1.1;letter-spacing:0;color:#00e054;}',
            '.letterboxd-user-status{min-width:0;margin:-.18rem 0 .38rem;font-size:.78rem;font-weight:700;line-height:1.25;color:var(--dimTextColor,rgba(255,255,255,.62));}',
            '.letterboxd-spoiler-warning{display:flex;align-items:center;justify-content:space-between;gap:.68rem;min-width:0;margin:.36rem 0 0;padding:.44rem .52rem .44rem .58rem;border:1px solid rgba(255,176,32,.28);border-radius:6px;background:rgba(255,176,32,.08);color:rgba(255,225,172,.9);font-size:.8rem;font-weight:750;line-height:1.32;}',
            '.letterboxd-spoiler-warning-text{min-width:0;overflow-wrap:anywhere;}',
            '.letterboxd-spoiler-toggle{display:inline-flex;flex:0 0 auto;align-items:center;justify-content:center;padding:.3rem .52rem;border:1px solid rgba(255,255,255,.16);border-radius:6px;background:rgba(255,255,255,.08);color:rgba(255,255,255,.86);font:inherit;font-size:.76rem;font-weight:800;line-height:1.15;cursor:pointer;white-space:nowrap;}',
            '.letterboxd-spoiler-toggle:hover,.letterboxd-spoiler-toggle:focus-visible{background:rgba(255,255,255,.13);outline:none;}',
            '.letterboxd-user-review{min-width:0;max-width:100%;overflow-wrap:anywhere;word-break:normal;margin:.32rem 0 0;padding:.02rem 0 0 .64rem;border-left:2px solid rgba(0,224,84,.72);font-size:.88rem;font-weight:500;line-height:1.42;color:rgba(255,255,255,.78);white-space:pre-wrap;}',
            '.letterboxd-user-review:before,.letterboxd-user-review:after{content:none;}',
            '@media (max-width:900px){.letterboxd-review-list{grid-template-columns:1fr;}}',
            '@media (max-width:700px){.letterboxd-social-widget{margin:1.25rem 0 1.8rem;padding:.85rem;}.letterboxd-user-heading{grid-template-columns:1fr;gap:.32rem;}.letterboxd-user-rating{justify-content:flex-start;min-width:0;}.letterboxd-spoiler-warning{align-items:flex-start;flex-direction:column;gap:.42rem;}.letterboxd-spoiler-toggle{width:100%;}}'
        ].join('');

        document.head.appendChild(style);
    }

    function createWidgetShell(page) {
        removeExisting(page);
        ensureWidgetStyles();

        const wrapper = document.createElement('section');
        wrapper.className = widgetClass + ' letterboxd-card-wrapper';
        wrapper.setAttribute('aria-label', 'Letterboxd friend reviews');

        const header = document.createElement('h3');
        header.className = 'letterboxd-widget-title';
        header.textContent = 'Letterboxd Friends';
        wrapper.appendChild(header);

        const list = document.createElement('div');
        list.className = 'letterboxd-review-list';
        wrapper.appendChild(list);

        return { wrapper, list };
    }

    function renderLoading(page) {
        const shell = createWidgetShell(page);

        const loading = document.createElement('div');
        loading.className = 'letterboxd-loading-state';
        loading.setAttribute('role', 'status');
        loading.setAttribute('aria-live', 'polite');

        const spinner = document.createElement('span');
        spinner.className = 'letterboxd-loading-spinner';
        spinner.setAttribute('aria-hidden', 'true');
        loading.appendChild(spinner);

        const text = document.createElement('span');
        text.textContent = 'Loading Letterboxd friend ratings and reviews...';
        loading.appendChild(text);

        shell.wrapper.appendChild(loading);
        placeWidget(page, shell.wrapper);
    }

    function createStarText(review) {
        const ratingValue = getValue(review, 'ratingValue', 'RatingValue');
        const starRating = getValue(review, 'starRating', 'StarRating') || '';
        const value = Number(ratingValue);
        if (!Number.isFinite(value) || value <= 0) {
            return starRating;
        }

        const full = Math.floor(value);
        const half = value - full >= 0.5;
        let stars = '★'.repeat(Math.max(0, Math.min(full, 5)));
        if (half && stars.length < 5) {
            stars += '½';
        }

        return stars || starRating;
    }

    function createStatusText(review) {
        const hasWatched = getValue(review, 'hasWatched', 'HasWatched');
        const watchedDate = getValue(review, 'watchedDate', 'WatchedDate');
        if (watchedDate && String(watchedDate).trim()) {
            return 'Watched ' + String(watchedDate).trim();
        }

        if (hasWatched !== false) {
            return 'Watched on Letterboxd';
        }

        return '';
    }

    function removeExisting(page) {
        page.querySelectorAll('.' + widgetClass).forEach(function (node) {
            node.remove();
        });
    }

    function renderReviews(page, reviews) {
        const shell = createWidgetShell(page);
        const wrapper = shell.wrapper;
        const list = shell.list;

        const normalizedReviews = normalizeReviews(reviews);
        if (normalizedReviews.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'letterboxd-empty-state';
            empty.textContent = 'No configured Letterboxd friend has logged this film yet.';
            list.appendChild(empty);
            placeWidget(page, wrapper);
            return;
        }

        normalizedReviews.forEach(function (review) {
            const card = document.createElement('article');
            card.className = 'letterboxd-user-card';
            const displayName = getValue(review, 'displayName', 'DisplayName');
            const username = getValue(review, 'username', 'Username');
            const avatarUrl = getValue(review, 'avatarUrl', 'AvatarUrl');
            const starRating = String(getValue(review, 'starRating', 'StarRating') || '');
            const reviewText = getValue(review, 'reviewText', 'ReviewText');
            const containsSpoilers = getValue(review, 'containsSpoilers', 'ContainsSpoilers') === true;
            const starText = createStarText(review);
            const statusText = createStatusText(review);

            const heading = document.createElement('div');
            heading.className = 'letterboxd-user-heading';

            const identity = document.createElement('div');
            identity.className = 'letterboxd-user-identity';

            if (avatarUrl && String(avatarUrl).trim()) {
                const avatar = document.createElement('img');
                avatar.className = 'letterboxd-user-avatar';
                avatar.src = String(avatarUrl).trim();
                avatar.alt = '';
                avatar.loading = 'lazy';
                avatar.decoding = 'async';
                avatar.referrerPolicy = 'no-referrer';
                avatar.addEventListener('error', function () {
                    avatar.remove();
                });
                identity.appendChild(avatar);
            }

            const name = document.createElement('span');
            name.className = 'letterboxd-user-name';
            name.textContent = displayName || username || 'Letterboxd user';
            identity.appendChild(name);
            heading.appendChild(identity);

            const rating = document.createElement('span');
            rating.className = 'letterboxd-user-rating';
            rating.textContent = starText || 'Watched';
            rating.setAttribute('aria-label', starRating ? starRating + ' stars' : 'Watched on Letterboxd');
            heading.appendChild(rating);

            card.appendChild(heading);

            if (statusText) {
                const status = document.createElement('div');
                status.className = 'letterboxd-user-status';
                status.textContent = statusText;
                card.appendChild(status);
            }

            let spoilerWarning = null;
            if (containsSpoilers) {
                const warning = document.createElement('div');
                warning.className = 'letterboxd-spoiler-warning';

                const warningText = document.createElement('span');
                warningText.className = 'letterboxd-spoiler-warning-text';
                warningText.textContent = 'This Letterboxd review contains spoilers.';
                warning.appendChild(warningText);

                card.appendChild(warning);
                spoilerWarning = warning;
            }

            const reviewTextValue = reviewText === null || reviewText === undefined ? '' : String(reviewText).trim();
            if (reviewTextValue) {
                const quote = document.createElement('blockquote');
                quote.className = 'letterboxd-user-review';
                quote.textContent = reviewTextValue;

                if (containsSpoilers) {
                    quote.hidden = true;

                    const toggle = document.createElement('button');
                    toggle.type = 'button';
                    toggle.className = 'letterboxd-spoiler-toggle';
                    toggle.textContent = 'Show spoiler review';
                    toggle.addEventListener('click', function () {
                        const shouldShow = quote.hidden;
                        quote.hidden = !shouldShow;
                        toggle.textContent = shouldShow ? 'Hide spoiler review' : 'Show spoiler review';
                    });
                    (spoilerWarning || card).appendChild(toggle);
                }

                card.appendChild(quote);
            }

            list.appendChild(card);
        });

        placeWidget(page, wrapper);
    }

    async function renderForCurrentPage() {
        if (!isMovieDetailPage()) {
            return;
        }

        const itemId = getCurrentItemId();
        if (!itemId) {
            return;
        }

        // This movie is already in flight or rendered. Re-place the widget if Jellyfin
        // re-rendered the page and dropped our DOM; never restart the fetch.
        if (activeItemId === itemId) {
            const livePage = getActiveDetailPage();
            if (livePage && !livePage.querySelector('.' + widgetClass)) {
                if (lastReviews) {
                    renderReviews(livePage, lastReviews);
                } else {
                    renderLoading(livePage);
                }
            }
            return;
        }

        const item = await getCurrentItem(itemId);
        const page = getActiveDetailPage();
        if (!page) {
            return;
        }

        if (item && item.Type && item.Type !== 'Movie') {
            removeExisting(page);
            return;
        }

        activeItemId = itemId;
        lastReviews = null;
        const myId = ++activeId;

        try {
            renderLoading(page);

            const reviews = await fetchReviews(itemId);
            if (activeId !== myId) {
                return;
            }

            lastReviews = normalizeReviews(reviews);
            const livePage = getActiveDetailPage();
            if (livePage) {
                renderReviews(livePage, lastReviews);
            }
        } catch (error) {
            console.warn('Letterboxd Social: failed to load reviews.', error);
            if (activeId === myId) {
                activeItemId = null;
                lastReviews = null;
            }
        }
    }

    function scheduleRender(delay) {
        window.clearTimeout(renderTimer);
        renderTimer = window.setTimeout(renderForCurrentPage, delay || renderDelay);
    }

    function onNavigation() {
        // Only reset state when the movie actually changed (or page isn't ready yet).
        // Jellyfin fires viewshow multiple times per page load; resetting unconditionally
        // would increment activeId mid-fetch and discard the result.
        const currentId = getCurrentItemId();
        if (!currentId || activeItemId !== currentId) {
            activeItemId = null;
            lastReviews = null;
        }
        scheduleRender(renderDelay);
        retryDelays.forEach(function (delay) {
            window.setTimeout(renderForCurrentPage, delay);
        });
    }

    document.addEventListener('viewshow', onNavigation);
    document.addEventListener('pageshow', onNavigation);
    document.addEventListener('DOMContentLoaded', onNavigation);
    window.addEventListener('hashchange', onNavigation);
    window.addEventListener('popstate', onNavigation);

    // Keep-alive only: if Jellyfin re-renders the detail page and drops the widget while a
    // movie is active, schedule a cheap re-place. Never reacts while the widget is present,
    // so our own renders cannot retrigger it.
    const observer = new MutationObserver(function () {
        if (activeItemId === null) {
            return;
        }

        const page = getActiveDetailPage();
        if (page && !page.querySelector('.' + widgetClass)) {
            scheduleRender(renderDelay);
        }
    });

    observer.observe(document.body || document.documentElement, {
        childList: true,
        subtree: true
    });

    onNavigation();
})();
