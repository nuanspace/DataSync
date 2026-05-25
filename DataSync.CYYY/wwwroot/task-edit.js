window.taskEdit = window.taskEdit || {};

window.taskEdit.focusInterfaceCard = function (interfaceKey) {
    if (!interfaceKey) {
        return;
    }

    const card = document.querySelector(`.interface-card[data-interface-key="${interfaceKey}"]`);
    if (!(card instanceof HTMLElement)) {
        return;
    }

    card.classList.remove("interface-card--spotlight");
    void card.offsetWidth;

    card.scrollIntoView({
        behavior: "smooth",
        block: "center"
    });

    card.classList.add("interface-card--spotlight");
    window.setTimeout(() => card.focus({ preventScroll: true }), 140);
    window.setTimeout(() => card.classList.remove("interface-card--spotlight"), 900);
};

window.taskEdit.focusNewInterfaceCard = function (selector) {
    const card = document.querySelector(selector);
    if (!card) {
        return;
    }

    card.scrollIntoView({
        behavior: "smooth",
        block: "center"
    });

    const input = card.querySelector("input, textarea");
    if (!(input instanceof HTMLElement)) {
        return;
    }

    window.setTimeout(() => input.focus(), 180);
};

window.dashboardOverview = window.dashboardOverview || {};

window.dashboardOverview.capture = function (gridSelector) {
    const grid = document.querySelector(gridSelector);
    const snapshot = {};
    if (!(grid instanceof HTMLElement)) {
        return snapshot;
    }

    grid.querySelectorAll("[data-overview-card-key]").forEach((item) => {
        if (!(item instanceof HTMLElement)) {
            return;
        }

        const key = item.getAttribute("data-overview-card-key");
        if (!key) {
            return;
        }

        const rect = item.getBoundingClientRect();
        snapshot[key] = {
            left: rect.left,
            top: rect.top
        };
    });

    return snapshot;
};

window.dashboardOverview.animate = function (gridSelector, previous) {
    const grid = document.querySelector(gridSelector);
    if (!(grid instanceof HTMLElement) || !previous) {
        return;
    }

    const items = Array.from(grid.querySelectorAll("[data-overview-card-key]"));
    let hasMove = false;

    items.forEach((item) => {
        if (!(item instanceof HTMLElement)) {
            return;
        }

        const key = item.getAttribute("data-overview-card-key");
        const oldRect = key ? previous[key] : null;
        if (!oldRect) {
            return;
        }

        const rect = item.getBoundingClientRect();
        const oldLeft = oldRect.left ?? oldRect.Left;
        const oldTop = oldRect.top ?? oldRect.Top;
        const x = oldLeft - rect.left;
        const y = oldTop - rect.top;
        if (Math.abs(x) < 0.5 && Math.abs(y) < 0.5) {
            return;
        }

        item.style.transition = "none";
        item.style.transform = `translate(${x}px, ${y}px)`;
        item.style.zIndex = "1";
        hasMove = true;
    });

    if (!hasMove) {
        return;
    }

    grid.classList.add("is-reordering");
    window.requestAnimationFrame(() => {
        items.forEach((item) => {
            if (!(item instanceof HTMLElement) || !item.style.transform) {
                return;
            }

            item.style.transition = "transform 720ms cubic-bezier(0.22, 1, 0.36, 1)";
            item.style.transform = "";
        });
    });

    window.setTimeout(() => {
        items.forEach((item) => {
            if (!(item instanceof HTMLElement)) {
                return;
            }

            item.style.transition = "";
            item.style.transform = "";
            item.style.zIndex = "";
        });
        grid.classList.remove("is-reordering");
    }, 820);
};
