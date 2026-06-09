(function () {
    'use strict';

    const pluginId = '449b09f6-a9b5-4cf0-ba7d-433c11e9c0ad';
    const pageId = 'letterboxdSocialConfigurationPage';
    const mappingsId = 'letterboxdUserMappings';
    const styleId = 'letterboxdSocialConfigurationStyles';

    function getPage() {
        return document.getElementById(pageId);
    }

    function getMappingsContainer() {
        return document.getElementById(mappingsId);
    }

    function getMappingValue(mapping, pascalName, camelName) {
        return mapping[pascalName] || mapping[camelName] || '';
    }

    function escapeAttribute(value) {
        return String(value || '')
            .replace(/&/g, '&amp;')
            .replace(/"/g, '&quot;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }

    function ensureStyles() {
        if (document.getElementById(styleId)) {
            return;
        }

        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = [
            '#letterboxdSocialConfigurationPage .content-primary{max-width:62rem;}',
            '#letterboxdSocialConfigurationPage .letterboxd-social-config-section{margin-bottom:2rem;}',
            '#letterboxdUserMappings{display:grid;gap:.75rem;}',
            '#letterboxdUserMappings:empty{display:none;}',
            '.letterboxd-social-config-row{display:grid;grid-template-columns:minmax(12rem,1fr) minmax(12rem,1fr) auto;align-items:end;gap:.85rem;padding:.85rem;background:var(--selectorBackgroundColorAlpha,rgba(31,35,42,.5));border:var(--defaultBorder,1px solid rgba(255,255,255,.12));border-radius:var(--smallRadius,.5rem);box-shadow:none;}',
            '.letterboxd-social-config-row .inputContainer{margin:0;}',
            '.letterboxd-social-config-row .inputLabel{font-weight:600;}',
            '.letterboxd-social-config-row input{width:100%;}',
            '.letterboxd-remove-user-mapping-button{display:inline-flex;align-items:center;justify-content:center;width:2.4rem;height:2.4rem;min-width:0;margin:0;padding:0;border:1px solid rgba(255,255,255,.16);border-radius:50%;background:rgba(255,255,255,.07);color:rgba(255,255,255,.72);font:inherit;font-size:1.35rem;font-weight:500;line-height:1;cursor:pointer;}',
            '.letterboxd-remove-user-mapping-button:hover,.letterboxd-remove-user-mapping-button:focus-visible{background:rgba(229,57,53,.16);border-color:rgba(229,57,53,.55);color:rgba(255,255,255,.94);outline:none;}',
            '.letterboxd-remove-user-mapping-button span{display:block;transform:translateY(-.06rem);}',
            '@media (max-width:760px){.letterboxd-social-config-row{grid-template-columns:1fr auto;align-items:end;}.letterboxd-social-config-row .inputContainer{grid-column:1 / 2;}.letterboxd-remove-user-mapping-button{grid-column:2 / 3;grid-row:1 / 3;align-self:center;}}'
        ].join('');

        document.head.appendChild(style);
    }

    function createMappingRow(mapping) {
        const row = document.createElement('div');
        row.className = 'letterboxd-social-config-row';

        const letterboxdUsername = escapeAttribute(getMappingValue(mapping, 'LetterboxdUsername', 'letterboxdUsername'));
        const displayName = escapeAttribute(getMappingValue(mapping, 'DisplayName', 'displayName'));

        row.innerHTML = [
            '<div class="inputContainer">',
            '<label class="inputLabel inputLabelUnfocused">Display name</label>',
            '<input class="emby-input letterboxd-config-display-name" type="text" autocomplete="off" value="' + displayName + '">',
            '</div>',
            '<div class="inputContainer">',
            '<label class="inputLabel inputLabelUnfocused">Letterboxd username</label>',
            '<input class="emby-input letterboxd-config-letterboxd-username" type="text" autocomplete="off" value="' + letterboxdUsername + '">',
            '</div>',
            '<button type="button" class="letterboxd-remove-user-mapping-button" aria-label="Remove Letterboxd account" title="Remove account">',
            '<span aria-hidden="true">&times;</span>',
            '</button>'
        ].join('');

        return row;
    }

    function addMapping(mapping) {
        const container = getMappingsContainer();
        if (!container) {
            return;
        }

        ensureStyles();
        container.appendChild(createMappingRow(mapping || {}));
    }

    function getNumberInputValue(id, fallback, minimum, maximum) {
        const input = document.getElementById(id);
        const value = Number(input ? input.value : fallback);

        if (!Number.isFinite(value)) {
            return fallback;
        }

        return Math.max(minimum, Math.min(maximum, Math.round(value)));
    }

    function normalizeMappings(config) {
        const mappings = config.UserMappings || config.userMappings || [];
        return Array.isArray(mappings) ? mappings : [];
    }

    function loadConfiguration() {
        const page = getPage();
        const container = getMappingsContainer();
        if (!page || !container || !window.ApiClient) {
            return;
        }

        ensureStyles();
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            const maxPagesInput = document.getElementById('maxDiaryPagesPerUser');
            const delayInput = document.getElementById('requestDelayMilliseconds');

            if (maxPagesInput) {
                maxPagesInput.value = config.MaxDiaryPagesPerUser || config.maxDiaryPagesPerUser || 3;
            }

            if (delayInput) {
                delayInput.value = config.RequestDelayMilliseconds ?? config.requestDelayMilliseconds ?? 1000;
            }

            container.innerHTML = '';

            const mappings = normalizeMappings(config);
            if (mappings.length === 0) {
                addMapping({});
                return;
            }

            mappings.forEach(addMapping);
        }).catch(function (error) {
            console.error('Letterboxd Social: failed to load plugin configuration.', error);
        }).finally(function () {
            Dashboard.hideLoadingMsg();
        });
    }

    function collectMappings() {
        const container = getMappingsContainer();
        if (!container) {
            return [];
        }

        return Array.from(container.querySelectorAll('.letterboxd-social-config-row')).map(function (row) {
            const letterboxdUsernameInput = row.querySelector('.letterboxd-config-letterboxd-username');
            const displayNameInput = row.querySelector('.letterboxd-config-display-name');
            const letterboxdUsername = letterboxdUsernameInput ? letterboxdUsernameInput.value.trim() : '';
            const displayName = displayNameInput ? displayNameInput.value.trim() : '';

            return {
                JellyfinUserId: '',
                LetterboxdUsername: letterboxdUsername,
                DisplayName: displayName || letterboxdUsername
            };
        }).filter(function (mapping) {
            return mapping.LetterboxdUsername.length > 0;
        });
    }

    function saveConfiguration(event) {
        event.preventDefault();
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.MaxDiaryPagesPerUser = getNumberInputValue('maxDiaryPagesPerUser', 3, 1, 25);
            config.RequestDelayMilliseconds = getNumberInputValue('requestDelayMilliseconds', 1000, 0, 30000);
            config.UserMappings = collectMappings();

            return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then(Dashboard.processPluginConfigurationUpdateResult).catch(function (error) {
            console.error('Letterboxd Social: failed to save plugin configuration.', error);
            Dashboard.alert({
                message: 'Letterboxd Social configuration could not be saved.'
            });
        }).finally(function () {
            Dashboard.hideLoadingMsg();
        });

        return false;
    }

    document.addEventListener('click', function (event) {
        const addButton = event.target.closest('#addLetterboxdUserMappingButton');
        if (addButton) {
            event.preventDefault();
            addMapping({});
            return;
        }

        const removeButton = event.target.closest('.letterboxd-remove-user-mapping-button');
        if (removeButton) {
            event.preventDefault();
            const row = removeButton.closest('.letterboxd-social-config-row');
            if (row) {
                row.remove();
            }
        }
    });

    document.addEventListener('submit', function (event) {
        if (event.target && event.target.id === 'letterboxdSocialConfigurationForm') {
            saveConfiguration(event);
        }
    });

    document.addEventListener('pageshow', function (event) {
        if (event.target && event.target.id === pageId) {
            loadConfiguration();
        }
    });

    document.addEventListener('viewshow', function () {
        if (getPage()) {
            loadConfiguration();
        }
    });

    document.addEventListener('DOMContentLoaded', function () {
        if (getPage()) {
            loadConfiguration();
        }
    });

    if (getPage()) {
        loadConfiguration();
    }
})();
