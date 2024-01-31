import { useSideMenu } from "../../../components/sideMenu/hooks/useSideMenu";
import { useKeepSideMenuOpen } from "../../../components/sideMenu/hooks/useKeepSideMenuOpen";
import { MenuItem, MenuLabel, NavigateButton } from "../../../../shared/components/sideBar/SideBarButtons";
import { ButtonGroup, ButtonGroups, Divider, SideBar } from "../../../../shared/components/sideBar/SideBar";
import { useLocation, useNavigate } from "react-router-dom";

export function CatalogSideBar() {
    const {currentMenu, toggleMenu} = useSideMenu();
    const {keepSideMenuOpen} = useKeepSideMenuOpen();
    const navigate = useNavigate();
    const {pathname} = useLocation()

    return (
        <SideBar>
            <NavigateButton path={'/'} data-qa-btn-home>
                <i className="sm sm-systemorph-fill"/>
            </NavigateButton>
            <ButtonGroups>
                <ButtonGroup>
                    <MenuItem small
                              active={pathname === '/'}
                              data-qa-btn-home2
                              onClick={() => navigate('/')}
                    >
                        <i className="sm sm-home"/>
                        <MenuLabel small text='home'/>
                    </MenuItem>
                    <MenuItem small
                              active={pathname === '/recent'}
                              data-qa-btn-recent
                              onClick={() => navigate('/recent')}
                    >
                        <i className="sm sm-clock"/>
                        <MenuLabel small text='recent'/>
                    </MenuItem>
                    <MenuItem small
                              active={pathname === '/public'}
                              data-qa-btn-public
                              onClick={() => navigate('/public')}
                    >
                        <i className="sm sm-cloud"/>
                        <MenuLabel small text='public'/>
                    </MenuItem>
                </ButtonGroup>
                <Divider/>
                <ButtonGroup>
                    <MenuItem
                        active={currentMenu === 'create'}
                        disabled={keepSideMenuOpen}
                        onClick={() => toggleMenu('create')} data-qa-btn-new-project>
                        <i className="sm sm-plus-circle"/>
                        <MenuLabel small text='new project'/>
                    </MenuItem>
                </ButtonGroup>
                <ButtonGroup>
                    
                    {/* TODO: Commented in task 24886, uncomment when needed (26/9/2022, avinokurov) */}
                    {/* <MenuItem small
                              active={currentMenu === 'account'}
                              disabled={keepSideMenuOpen}
                              // onClick={() => toggleMenu('account')}
                              data-qa-btn-account>
                        <i className="sm sm-user"/>
                        <MenuLabel small text='account'/>
                    </MenuItem> */}
                </ButtonGroup>
            </ButtonGroups>
        </SideBar>
    );
}