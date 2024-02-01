import { Subscription } from "./billingApi";
import styles from "./billing-address.module.scss";
import classNames from "classnames";
import { identity } from "lodash";

interface Props {
    subscription: Subscription;
    printLayout?: boolean;
}

export function BillingAddress({subscription, printLayout}: Props) {
    const {
        company,
        address1,
        address2,
        zip,
        city,
        country,
        contactPerson,
        contactPersonEmail,
        contactPersonPhone
    } = subscription;

    const zipCityCountry = [zip, city, country].filter(identity).join(' ');
    const hasAddress = company || address1 || address2 || zipCityCountry;
    const hasContactInfo = contactPerson || contactPersonPhone || contactPersonEmail;

    if (!hasAddress && !hasContactInfo) {
        return null;
    }

    return (
        <div className={styles.container}>
            {!printLayout && <h3 className={styles.title}>Billing address</h3>}
            {hasAddress &&
                <div className={styles.row}>
                    <i className={classNames(styles.icon, 'sm sm-briefcase')}/>
                    <address className={styles.column}>
                        {company && <div>{company}</div>}
                        {address1 && <div className={styles.addressLine}>{address1}</div>}
                        {address2 &&
                          <div>{address2}</div>
                        }
                      <div>{zipCityCountry}</div>
                    </address>
                </div>
            }

            {hasContactInfo &&
                <div className={styles.row}>
                    <i className={classNames(styles.icon, 'sm sm-user')}/>
                    <address className={styles.column}>
                        {contactPerson && <div className={styles.name}>{contactPerson}</div>}
                        {contactPersonPhone &&
                          <a className={styles.phone} href={`tel:${contactPersonPhone}`}>{contactPersonPhone}</a>}
                        {contactPersonEmail &&
                          <a className={styles.mail} href={`mailto:${contactPersonEmail}`}>{contactPersonEmail}</a>}
                    </address>
                </div>
            }
        </div>
    );
}