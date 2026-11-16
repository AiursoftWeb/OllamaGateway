const virtualModelEditLocData = document.getElementById('virtual-model-edit-loc-data');
        const getLocalizedText = (key, fallback) => virtualModelEditLocData.querySelector(`span[data-key="${key}"]`)?.innerText || fallback;

        document.querySelectorAll('.backend-toggle-confirm').forEach(function (checkbox) {
            checkbox.addEventListener('click', function (event) {
                if (confirm(getLocalizedText('toggle-confirm', 'Toggle this backend?'))) {
                    document.getElementById(checkbox.dataset.formId).submit();
                } else {
                    event.preventDefault();
                }
            });
        });
        document.querySelectorAll('.delete-backend-form').forEach(function (form) {
            form.addEventListener('submit', function (event) {
                if (!confirm(getLocalizedText('delete-confirm', 'Are you sure?'))) {
                    event.preventDefault();
                }
            });
        });
        document.getElementById('providerSelect').addEventListener('change', function () {
            window.location.href = this.dataset.navigationPrefix + encodeURIComponent(this.value);
        });
